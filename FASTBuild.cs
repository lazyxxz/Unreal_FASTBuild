// Copyright 2018 Yassine Riahi and Liam Flookes. Provided under a MIT License, see license file on github.
// Used to generate a fastbuild .bff file from UnrealBuildTool to allow caching and distributed builds. 
// Tested with Windows 10, Visual Studio 2015/2017, Unreal Engine 4.19.1, FastBuild v0.95
// Durango is fully supported (Compiles with VS2015).
// Orbis will likely require some changes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using Tools.DotNETCommon;

namespace UnrealBuildTool
{
	class FASTBuild : ActionExecutor
	{
		/*---- Configurable User settings ----*/

		// Used to specify a non-standard location for the FBuild.exe, for example if you have not added it to your PATH environment variable.
		public static string FBuildExePathOverride = "";

		public bool bCleanBuild = false;

		// Controls network build distribution
		private bool bEnableDistribution = true;

		// Controls whether to use caching at all. CachePath and CacheMode are only relevant if this is enabled.
		private bool bEnableCaching = true;

		// Location of the shared cache, it could be a local or network path (i.e: @"\\DESKTOP-BEAST\FASTBuildCache").
		// Only relevant if bEnableCaching is true;
		private string CachePath = @"\\SharedDrive\FASTBuildCache";

		public enum eCacheMode
		{
			ReadWrite, // This machine will both read and write to the cache
			ReadOnly,  // This machine will only read from the cache, use for developer machines when you have centralized build machines
			WriteOnly, // This machine will only write from the cache, use for build machines when you have centralized build machines
		}

		// Cache access mode
		// Only relevant if bEnableCaching is true;
		private eCacheMode CacheMode = eCacheMode.ReadWrite;

		/*--------------------------------------*/

		public override string Name
		{
			get { return "FASTBuild"; }
		}

		public static bool IsAvailable()
		{
			if (FBuildExePathOverride != "")
			{
				return File.Exists(FBuildExePathOverride);
			}

			// Get the name of the FASTBuild executable.
			string fbuild = "fbuild";
			if (BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Win64)
			{
				fbuild = "fbuild.exe";
			}

			// Search the path for it
			string PathVariable = Environment.GetEnvironmentVariable("PATH");
			foreach (string SearchPath in PathVariable.Split(Path.PathSeparator))
			{
				try
				{
					string PotentialPath = Path.Combine(SearchPath, fbuild);
					if (File.Exists(PotentialPath))
					{
						return true;
					}
				}
				catch (ArgumentException)
				{
					// PATH variable may contain illegal characters; just ignore them.
				}
			}
			return false;
		}

		private HashSet<string> ForceLocalCompileModules = new HashSet<string>()
						 {"Module.ProxyLODMeshReduction",
							"GoogleVRController"};

		private enum FBBuildType
		{
			Windows,
			XBOne,
			PS4
		}

		private FBBuildType BuildType = FBBuildType.Windows;

		private void DetectBuildType(List<Action> Actions)
		{
			foreach (Action action in Actions)
			{
				if (action.ActionType != ActionType.Compile && action.ActionType != ActionType.Link)
					continue;

				if (action.CommandPath.FullName.Contains("orbis"))
				{
					BuildType = FBBuildType.PS4;
					return;
				}
				else if (action.CommandArguments.Contains("Intermediate\\Build\\XboxOne"))
				{
					BuildType = FBBuildType.XBOne;
					return;
				}
				else if (action.CommandPath.FullName.Contains("Microsoft")) //Not a great test.
				{
					BuildType = FBBuildType.Windows;
					return;
				}
			}
		}

		private bool IsMSVC() { return BuildType == FBBuildType.Windows || BuildType == FBBuildType.XBOne; }
		private bool IsPS4() { return BuildType == FBBuildType.PS4; }
		private bool IsXBOnePDBUtil(Action action) { return action.CommandPath.FullName.Contains("XboxOnePDBFileUtil.exe"); }
		private bool IsPS4SymbolTool(Action action) { return action.CommandPath.FullName.Contains("PS4SymbolTool.exe"); }
		private string GetCompilerName()
		{
			switch (BuildType)
			{
				default:
				case FBBuildType.XBOne:
				case FBBuildType.Windows: return "UE4Compiler";
				case FBBuildType.PS4: return "UE4PS4Compiler";
			}
		}

		//Run FASTBuild on the list of actions. Relies on fbuild.exe being in the path.
		public override bool ExecuteActions(List<Action> Actions, bool bLogDetailedActionStats)
		{
			bool FASTBuildResult = true;
			if (Actions.Count > 0)
			{
				DetectBuildType(Actions);

				string FASTBuildFilePath = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Intermediate", "Build", "fbuild.bff");

				List<Action> LocalExecutorActions = new List<Action>();

				if (CreateBffFile(Actions, FASTBuildFilePath, LocalExecutorActions))
				{
					FASTBuildResult = ExecuteBffFile(FASTBuildFilePath);

					if (FASTBuildResult)
					{
						LocalExecutor localExecutor = new LocalExecutor();
						FASTBuildResult = localExecutor.ExecuteActions(LocalExecutorActions, bLogDetailedActionStats);
					}
				}
				else
				{
					FASTBuildResult = false;
				}
			}

			return FASTBuildResult;
		}

		private void AddText(string StringToWrite)
		{
			byte[] Info = new System.Text.UTF8Encoding(true).GetBytes(StringToWrite);
			bffOutputFileStream.Write(Info, 0, Info.Length);
		}


		private string SubstituteEnvironmentVariables(string commandLineString)
		{
			string outputString = commandLineString.Replace("$(DurangoXDK)", "$DurangoXDK$");
			outputString = outputString.Replace("$(SCE_ORBIS_SDK_DIR)", "$SCE_ORBIS_SDK_DIR$");
			outputString = outputString.Replace("$(DXSDK_DIR)", "$DXSDK_DIR$");
			outputString = outputString.Replace("$(CommonProgramFiles)", "$CommonProgramFiles$");

			return outputString;
		}

		private Dictionary<string, string> ParseCommandLineOptions(string CompilerCommandLine, string[] SpecialOptions, bool SaveResponseFile = false, bool SkipInputFile = false)
		{
			Dictionary<string, string> ParsedCompilerOptions = new Dictionary<string, string>();

			// Make sure we substituted the known environment variables with corresponding BFF friendly imported vars
			CompilerCommandLine = SubstituteEnvironmentVariables(CompilerCommandLine);

			// Some tricky defines /DTROUBLE=\"\\\" abc  123\\\"\" aren't handled properly by either Unreal or Fastbuild, but we do our best.
			char[] SpaceChar = { ' ' };
			string[] RawTokens = CompilerCommandLine.Trim().Split(' ');
			List<string> ProcessedTokens = new List<string>();
			bool QuotesOpened = false;
			string PartialToken = "";
			string ResponseFilePath = "";

			string responseCommandline = "";
			for (int i = 0; i < RawTokens.Count(); ++i)
			{
				if (RawTokens[i].Length >= 1 && RawTokens[i].StartsWith("@\""))
				{
					responseCommandline = RawTokens[i];
				}
			}

			//if (RawTokens.Length >= 1 && RawTokens[0].StartsWith("@\"")) //Response files are in 4.13 by default. Changing VCToolChain to not do this is probably better.
			if (responseCommandline.Length >= 1 && responseCommandline.StartsWith("@\"")) //Response files are in 4.13 by default. Changing VCToolChain to not do this is probably better.
			{
				//string responseCommandline = RawTokens[0];

				//// If we had spaces inside the response file path, we need to reconstruct the path.
				//for (int i = 1; i < RawTokens.Length; ++i)
				//{
				//	responseCommandline += " " + RawTokens[i];
				//}

				ResponseFilePath = responseCommandline.Substring(2, responseCommandline.Length - 3); // bit of a bodge to get the @"response.txt" path...
				try
				{
					string ResponseFileText = File.ReadAllText(ResponseFilePath);

					// Make sure we substituted the known environment variables with corresponding BFF friendly imported vars
					ResponseFileText = SubstituteEnvironmentVariables(ResponseFileText);

					string[] Separators = { "\n", " ", "\r" };
					if (File.Exists(ResponseFilePath))
						RawTokens = ResponseFileText.Split(Separators, StringSplitOptions.RemoveEmptyEntries); //Certainly not ideal 
					if (RawTokens.Count() >= 2 && RawTokens[0] == "-h" && RawTokens[1].Contains(".ispc.generated."))
					{
						RawTokens[0] = string.Format("{0} {1}", RawTokens[0], RawTokens[1]);
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("Looks like a response file in: " + CompilerCommandLine + ", but we could not load it! " + e.Message);
					ResponseFilePath = "";
				}
			}

			// Raw tokens being split with spaces may have split up some two argument options and 
			// paths with multiple spaces in them also need some love
			for (int i = 0; i < RawTokens.Length; ++i)
			{
				string Token = RawTokens[i];
				if (string.IsNullOrEmpty(Token))
				{
					if (ProcessedTokens.Count > 0 && QuotesOpened)
					{
						string CurrentToken = ProcessedTokens.Last();
						CurrentToken += " ";
					}

					continue;
				}

				int numQuotes = 0;
				// Look for unescaped " symbols, we want to stick those strings into one token.
				for (int j = 0; j < Token.Length; ++j)
				{
					if (Token[j] == '\\') //Ignore escaped quotes
						++j;
					else if (Token[j] == '"')
						numQuotes++;
				}

				// Defines can have escaped quotes and other strings inside them
				// so we consume tokens until we've closed any open unescaped parentheses.
				if ((Token.StartsWith("/D") || Token.StartsWith("-D")) && !QuotesOpened)
				{
					if (numQuotes == 0 || numQuotes == 2)
					{
						ProcessedTokens.Add(Token);
					}
					else
					{
						PartialToken = Token;
						++i;
						bool AddedToken = false;
						for (; i < RawTokens.Length; ++i)
						{
							string NextToken = RawTokens[i];
							if (string.IsNullOrEmpty(NextToken))
							{
								PartialToken += " ";
							}
							else if (!NextToken.EndsWith("\\\"") && NextToken.EndsWith("\"")) //Looking for a token that ends with a non-escaped "
							{
								ProcessedTokens.Add(PartialToken + " " + NextToken);
								AddedToken = true;
								break;
							}
							else
							{
								PartialToken += " " + NextToken;
							}
						}
						if (!AddedToken)
						{
							Console.WriteLine("Warning! Looks like an unterminated string in tokens. Adding PartialToken and hoping for the best. Command line: " + CompilerCommandLine);
							ProcessedTokens.Add(PartialToken);
						}
					}
					continue;
				}

				if (!QuotesOpened)
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						PartialToken = Token + " ";
						QuotesOpened = true;
					}
					else
					{
						ProcessedTokens.Add(Token);
					}
				}
				else
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						ProcessedTokens.Add(PartialToken + Token);
						QuotesOpened = false;
					}
					else
					{
						PartialToken += Token + " ";
					}
				}
			}

			//Processed tokens should now have 'whole' tokens, so now we look for any specified special options
			foreach (string specialOption in SpecialOptions)
			{
				for (int i = 0; i < ProcessedTokens.Count; ++i)
				{
					if (ProcessedTokens[i] == specialOption && i + 1 < ProcessedTokens.Count)
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i + 1];
						ProcessedTokens.RemoveRange(i, 2);
						break;
					}
					else if (ProcessedTokens[i].StartsWith(specialOption))
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i].Replace(specialOption, null);
						ProcessedTokens.RemoveAt(i);
						break;
					}
				}
			}

			//The search for the input file... we take the first non-argument we can find
			if (!SkipInputFile)
			{
				for (int i = 0; i < ProcessedTokens.Count; ++i)
				{
					string Token = ProcessedTokens[i];
					if (Token.Length == 0)
					{
						continue;
					}

					if (Token == "/I" || Token == "/l" || Token == "/D" || Token == "-D" || Token == "-x" || Token == "-include") // Skip tokens with values, I for cpp includes, l for resource compiler includes
					{
						++i;
					}
					else if (!Token.StartsWith("/") && !Token.StartsWith("-") && !Token.StartsWith("\"-"))
					{
						ParsedCompilerOptions["InputFile"] = Token;
						ProcessedTokens.RemoveAt(i);
						break;
					}
				}
			}

			ParsedCompilerOptions["OtherOptions"] = string.Join(" ", ProcessedTokens) + " ";

			if (SaveResponseFile && !string.IsNullOrEmpty(ResponseFilePath))
			{
				ParsedCompilerOptions["@"] = ResponseFilePath;
			}

			return ParsedCompilerOptions;
		}

		//private void AddPrerequisiteActions(List<Action> Actions, List<Action> InActions, HashSet<int> UsedActions, Action Action)
		//{
		//	int ActionIndex = InActions.IndexOf(Action);
		//	if (UsedActions.Contains(ActionIndex))
		//	{
		//		return;
		//	}

		//	foreach (Action PrerequisiteAction in Action.PrerequisiteActions)
		//	{
		//		int DepIndex = InActions.IndexOf(PrerequisiteAction);
		//		if (UsedActions.Contains(DepIndex))
		//		{
		//			continue;
		//		}
		//		AddPrerequisiteActions(Actions, InActions, UsedActions, PrerequisiteAction);
		//	}

		//	Actions.Add(Action);
		//	UsedActions.Add(ActionIndex);			
		//}

		private void AddPrerequisiteActions(List<Action> Actions, List<Action> InActions, HashSet<int> UsedActions, Action Action)
		{
			int ActionIndex = InActions.IndexOf(Action);
			if (UsedActions.Contains(ActionIndex))
			{
				return;
			}

			HashSet<int> PushedActions = new HashSet<int>();
			Stack<Action> ActionStack = new Stack<Action>();
			ActionStack.Push(Action);

			while (ActionStack.Count() > 0)
			{
				Action TopAction = ActionStack.Peek();

				int TopActionIndex = InActions.IndexOf(TopAction);
				if (UsedActions.Contains(TopActionIndex) || TopAction == null)
				{
					ActionStack.Pop();
				}
				else if (TopAction.PrerequisiteActions.Count() == 0 || PushedActions.Contains(TopActionIndex))
				{
					Actions.Add(TopAction);
					UsedActions.Add(TopActionIndex);
					ActionStack.Pop();
				}
				else
				{
					PushedActions.Add(TopActionIndex);
					foreach (Action PrerequisiteAction in TopAction.PrerequisiteActions.Reverse())
					{
						if (PrerequisiteAction != null)
						{
							ActionStack.Push(PrerequisiteAction);
						}
					}
				}
			}	
		}

		private List<Action> SortActions(List<Action> InActions)
		{
			List<Action> Actions = InActions;

			int NumSortErrors = 0;
			for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
			{
				Action Action = InActions[ActionIndex];
				foreach (Action PrerequisiteAction in Action.PrerequisiteActions)
				{
					int DepIndex = InActions.IndexOf(PrerequisiteAction);
					if (DepIndex > ActionIndex)
					{
						NumSortErrors++;
					}
				}
			}
			if (NumSortErrors > 0)
			{
				Actions = new List<Action>();
				var UsedActions = new HashSet<int>();
				for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
				{
					AddPrerequisiteActions(Actions, InActions, UsedActions, InActions[ActionIndex]);

					//if (UsedActions.Contains(ActionIndex))
					//{
					//	continue;
					//}
					//Action Action = InActions[ActionIndex];
					//foreach (Action PrerequisiteAction in Action.PrerequisiteActions)
					//{
					//	int DepIndex = InActions.IndexOf(PrerequisiteAction);
					//	if (UsedActions.Contains(DepIndex))
					//	{
					//		continue;
					//	}
					//	Actions.Add(PrerequisiteAction);
					//	UsedActions.Add(DepIndex);
					//}
					//Actions.Add(Action);
					//UsedActions.Add(ActionIndex);
				}
				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					Action Action = Actions[ActionIndex];
					foreach (Action PrerequisiteAction in Action.PrerequisiteActions)
					{
						int DepIndex = Actions.IndexOf(PrerequisiteAction);
						if (DepIndex > ActionIndex)
						{
							Console.WriteLine("Action is not topologically sorted.");
							Console.WriteLine("  {0} {1}", Action.CommandPath, Action.CommandArguments);
							Console.WriteLine("Dependency");
							Console.WriteLine("  {0} {1}", PrerequisiteAction.CommandPath, PrerequisiteAction.CommandArguments);
							throw new BuildException("Cyclical Dependency in action graph.");
						}
					}
				}
			}

			return Actions;
		}

		private string GetOptionValue(Dictionary<string, string> OptionsDictionary, string Key, Action Action, bool ProblemIfNotFound = false)
		{
			string Value = string.Empty;
			if (OptionsDictionary.TryGetValue(Key, out Value))
			{
				return Value.Trim(new Char[] { '\"' });
			}

			if (ProblemIfNotFound)
			{
				Console.WriteLine("We failed to find " + Key + ", which may be a problem.");
				Console.WriteLine("Action.CommandArguments: " + Action.CommandArguments);
			}

			return Value;
		}

		public string GetRegistryValue(string keyName, string valueName, object defaultValue)
		{
			object returnValue = (string)Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\SOFTWARE\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = (string)Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Wow6432Node\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\SOFTWARE\\Wow6432Node\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			return defaultValue.ToString();
		}

		VCEnvironment VCEnv = null;
		private void WriteEnvironmentSetup()
		{
			DirectoryReference VCInstallDir = null;
			string VCToolPath64 = "";

			if (VCEnv == null)
			{
				try
				{
					// This may fail if the caller emptied PATH; we try to ignore the problem since
					// it probably means we are building for another platform.
					if (BuildType == FBBuildType.Windows)
					{
						VCEnv = VCEnvironment.Create(WindowsPlatform.GetDefaultCompiler(null), UnrealTargetPlatform.Win64, WindowsArchitecture.x64, null, null, null);
					}
					else if (BuildType == FBBuildType.XBOne)
					{
						// If you have XboxOne source access, uncommenting the line below will be better for selecting the appropriate version of the compiler.
						// Translate the XboxOne compiler to the right Windows compiler to set the VC environment vars correctly...
						//WindowsCompiler windowsCompiler = Compiler == XboxOneCompiler.VisualStudio2015 ? WindowsCompiler.VisualStudio2019 : WindowsCompiler.VisualStudio2017;
						//WindowsCompiler windowsCompiler = VCEnvironment;
						VCEnv = VCEnvironment.Create(WindowsPlatform.GetDefaultCompiler(null), UnrealTargetPlatform.Win64, WindowsArchitecture.x64, null, null, null);
					}
				}
				catch (Exception)
				{
					Console.WriteLine("Failed to get Visual Studio environment.");
				}
			}

			// Copy environment into a case-insensitive dictionary for easier key lookups
			Dictionary<string, string> envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
			{
				envVars[(string)entry.Key] = (string)entry.Value;
			}

			if (envVars.ContainsKey("CommonProgramFiles"))
			{
				AddText("#import CommonProgramFiles\n");
			}

			if (envVars.ContainsKey("DXSDK_DIR"))
			{
				AddText("#import DXSDK_DIR\n");
			}

			if (envVars.ContainsKey("DurangoXDK"))
			{
				AddText("#import DurangoXDK\n");
			}

			if (VCEnv != null)
			{
				string platformVersionNumber = "VSVersionUnknown";

				switch (VCEnv.Compiler)
				{
					case WindowsCompiler.VisualStudio2015_DEPRECATED:
						platformVersionNumber = "140";
						break;

					case WindowsCompiler.VisualStudio2017:
						// For now we are working with the 140 version, might need to change to 141 or 150 depending on the version of the Toolchain you chose
						// to install
						platformVersionNumber = "140";
						break;

					case WindowsCompiler.VisualStudio2019:
						// For now we are working with the 140 version, might need to change to 141 or 150 depending on the version of the Toolchain you chose
						// to install
						platformVersionNumber = "142";
						break;

					default:
						string exceptionString = "Error: Unsupported Visual Studio Version.";
						Console.WriteLine(exceptionString);
						throw new BuildException(exceptionString);
				}


				if (!WindowsPlatform.TryGetVSInstallDir(WindowsPlatform.GetDefaultCompiler(null), out VCInstallDir))
				{
					string exceptionString = "Error: Cannot locate Visual Studio Installation.";
					Console.WriteLine(exceptionString);
					throw new BuildException(exceptionString);
				}

				//VCToolPath64 = VCEnvironment.GetVCToolPath64(WindowsPlatform.GetDefaultCompiler(null), VCEnv.ToolChainDir).ToString();
				VCToolPath64 = VCEnv.CompilerPath.ToString();

				string debugVCToolPath64 = VCEnv.CompilerPath.Directory.ToString();

				string ThirdPartyBinaries = "..\\Source\\ThirdParty";
				AddText(string.Format("\n.ISPC = '{0}'\n\n", ThirdPartyBinaries + "\\IntelISPC\\bin\\Windows\\ispc.exe"));

				AddText(string.Format(".WindowsSDKBasePath = '{0}'\n", VCEnv.WindowsSdkDir));

				AddText(              "Compiler('UE4ResourceCompiler') \n{\n");
				AddText(string.Format("\t.Executable = '{0}'\n", VCEnv.ResourceCompilerPath));
				AddText(              "\t.CompilerFamily  = 'custom'\n");
				AddText(              "}\n\n");


				AddText("Compiler('UE4Compiler') \n{\n");

				//AddText(string.Format("\t.Root = '{0}'\n", "$VS_TOOLCHAIN_Root$"));
				AddText(string.Format("\t.Root = '{0}'\n", VCEnv.CompilerPath.Directory));
				AddText("\t.Executable = '$Root$/cl.exe'\n");

				if (VCEnv.Compiler == WindowsCompiler.VisualStudio2015_DEPRECATED || VCEnv.Compiler == WindowsCompiler.VisualStudio2017)
				{
					AddText("\t.ExtraFiles =\n\t{\n");
					AddText("\t\t'$Root$/c1.dll'\n");
					AddText("\t\t'$Root$/c1xx.dll'\n");
					AddText("\t\t'$Root$/c2.dll'\n");

					if (File.Exists(FileReference.Combine(VCEnv.CompilerPath.Directory, "1033/clui.dll").ToString())) //Check English first...
					{
						AddText("\t\t'$Root$/1033/clui.dll'\n");
					}
					else
					{
						var numericDirectories = Directory.GetDirectories(VCToolPath64).Where(d => Path.GetFileName(d).All(char.IsDigit));
						var cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any());
						if (cluiDirectories.Any())
						{
							AddText(string.Format("\t\t'$Root$/{0}/clui.dll'\n", Path.GetFileName(cluiDirectories.First())));
						}
					}
					AddText("\t\t'$Root$/mspdbsrv.exe'\n");
					AddText("\t\t'$Root$/mspdbcore.dll'\n");

					AddText(string.Format("\t\t'$Root$/mspft{0}.dll'\n", platformVersionNumber));
					AddText(string.Format("\t\t'$Root$/msobj{0}.dll'\n", platformVersionNumber));

					if (VCEnv.Compiler == WindowsCompiler.VisualStudio2015_DEPRECATED)
					{
						AddText(string.Format("\t\t'$Root$/mspdb{0}.dll'\n", platformVersionNumber));
						AddText(string.Format("\t\t'{0}/VC/redist/x64/Microsoft.VC{1}.CRT/msvcp{2}.dll'\n", VCInstallDir.ToString(), platformVersionNumber, platformVersionNumber));
						AddText(string.Format("\t\t'{0}/VC/redist/x64/Microsoft.VC{1}.CRT/vccorlib{2}.dll'\n", VCInstallDir.ToString(), platformVersionNumber, platformVersionNumber));
					}
					else if (VCEnv.Compiler == WindowsCompiler.VisualStudio2017)
					{
						//VS 2017 is really confusing in terms of version numbers and paths so these values might need to be modified depending on what version of the tool chain you
						// chose to install.
						AddText(string.Format("\t\t'$Root$/mspdb{0}.dll'\n", platformVersionNumber));
						AddText(string.Format("\t\t'{0}/VC/Redist/MSVC/14.13.26020/x64/Microsoft.VC141.CRT/msvcp{1}.dll'\n", VCInstallDir.ToString(), platformVersionNumber));
						AddText(string.Format("\t\t'{0}/VC/Redist/MSVC/14.13.26020/x64/Microsoft.VC141.CRT/vccorlib{1}.dll'\n", VCInstallDir.ToString(), platformVersionNumber));
					}
				}
				else
				{
					String[] clFiles =
					{
						"$Root$/c1.dll",
						"$Root$/c1xx.dll",
						"$Root$/c2.dll",
						"$Root$/msobj140.dll",
						"$Root$/mspdb140.dll",
						"$Root$/mspdbcore.dll",
						"$Root$/mspdbsrv.exe",
						"$Root$/mspft140.dll",
						"$Root$/msvcp140.dll",
						"$Root$/msvcp140_atomic_wait.dll",
						"$Root$/tbbmalloc.dll", // Required as of 16.2 (14.22.27905)
						"$Root$/vcruntime140.dll",
						"$Root$/vcruntime140_1.dll", // Required as of 16.5.1 (14.25.28610)
						"$Root$/1033/clui.dll",
						"$Root$/1033/mspft140ui.dll", // Localized messages for static analysis
					};
					AddText("\t.ExtraFiles =\n\t{\n");
					foreach (string item in clFiles)
					{
						AddText(string.Format("\t\t'{0}'\n", item));
					}

					//AddText(string.Format("\t\t'$Root$/c1.dll'\n", platformVersionNumber));
					//AddText(string.Format("\t\t'$Root$/mspdb140.dll'\n", platformVersionNumber));

					//AddText(string.Format("\t\t'{0}/VC/Redist/MSVC/14.16.27012/x64/Microsoft.VC141.CRT/msvcp140.dll'\n", VCInstallDir.ToString()));
					//AddText(string.Format("\t\t'{0}/VC/Redist/MSVC/14.16.27012/x64/Microsoft.VC141.CRT/msvcp140_1.dll'\n", VCInstallDir.ToString()));
					//AddText(string.Format("\t\t'{0}/VC/Redist/MSVC/14.16.27012/x64/Microsoft.VC141.CRT/msvcp140_2.dll'\n", VCInstallDir.ToString()));
					//AddText(string.Format("\t\t'{0}/VC/Redist/MSVC/14.16.27012/x64/Microsoft.VC141.CRT/vccorlib.dll'\n", VCInstallDir.ToString()));
				}

				AddText("\t}\n"); //End extra files

				AddText("}\n\n"); //End compiler
			}

			if (envVars.ContainsKey("SCE_ORBIS_SDK_DIR"))
			{
				AddText(string.Format(".SCE_ORBIS_SDK_DIR = '{0}'\n", envVars["SCE_ORBIS_SDK_DIR"]));
				AddText(string.Format(".PS4BasePath = '{0}/host_tools/bin'\n\n", envVars["SCE_ORBIS_SDK_DIR"]));
				AddText("Compiler('UE4PS4Compiler') \n{\n");
				AddText("\t.Executable = '$PS4BasePath$/orbis-clang.exe'\n");
				AddText("\t.ExtraFiles = '$PS4BasePath$/orbis-snarl.exe'\n");
				AddText("}\n\n");
			}

			AddText("Settings \n{\n");

			// Optional cachePath user setting
			if (bEnableCaching && CachePath != "")
			{
				AddText(string.Format("\t.CachePath = '{0}'\n", CachePath));
			}

			//Start Environment
			AddText("\t.Environment = \n\t{\n");
			if (VCEnv != null)
			{
				AddText(string.Format("\t\t\"PATH={0}\\Common7\\IDE\\;{1}\\bin\\{2}\\x64;{3}\",\n"
					, VCInstallDir.ToString()
					, VCEnv.WindowsSdkDir, VCEnv.WindowsSdkVersion
					, VCToolPath64));
				if (VCEnv.IncludePaths.Count() > 0)
				{
					AddText(string.Format("\t\t\"INCLUDE={0}\",\n", String.Join(";", VCEnv.IncludePaths.Select(x => x))));
				}

				if (VCEnv.LibraryPaths.Count() > 0)
				{
					AddText(string.Format("\t\t\"LIB={0}\",\n", String.Join(";", VCEnv.LibraryPaths.Select(x => x))));
				}
			}
			if (envVars.ContainsKey("TMP"))
				AddText(string.Format("\t\t\"TMP={0}\",\n", envVars["TMP"]));
			if (envVars.ContainsKey("SystemRoot"))
				AddText(string.Format("\t\t\"SystemRoot={0}\",\n", envVars["SystemRoot"]));
			if (envVars.ContainsKey("INCLUDE"))
				AddText(string.Format("\t\t\"INCLUDE={0}\",\n", envVars["INCLUDE"]));
			if (envVars.ContainsKey("LIB"))
				AddText(string.Format("\t\t\"LIB={0}\",\n", envVars["LIB"]));

			AddText("\t}\n"); //End environment
			AddText("}\n\n"); //End Settings
		}

		private void AddISPCCompileAction(Action Action, int ActionIndex, List<int> DependencyIndices)
		{
			string[] SpecialCompilerOptions = { "/Fo", "/fo", "/Yc", "/Yu", "/Fp", "-o", "-h" };
			var ParsedCompilerOptions = ParseCommandLineOptions(Action.CommandArguments, SpecialCompilerOptions);

			char[] flag = { ' ', '\\', '/', '\"' };
			string OutFlag = "-h";
			string OutputObjectFileNameTemp = GetOptionValue(ParsedCompilerOptions, "-h", Action, ProblemIfNotFound: false);
			if (OutputObjectFileNameTemp == null)
			{
				OutFlag = "-o";
				OutputObjectFileNameTemp = GetOptionValue(ParsedCompilerOptions, "-o", Action, ProblemIfNotFound: true);
			}
			string OutputObjectFileName = OutputObjectFileNameTemp.TrimStart(flag);
			string OtherCompilerOptions = GetOptionValue(ParsedCompilerOptions, "OtherOptions", Action);


			string[] RawTokens = Action.CommandArguments.Trim().Split(' ');
			char[] flag2 = { ' ', '\\', '/', '\"', '@' };
			string InputFile = RawTokens[0].TrimStart(flag2).TrimEnd(flag2);
			if (Path.GetExtension(InputFile) != ".ispc")
			{
				InputFile = GetOptionValue(ParsedCompilerOptions, "InputFile", Action, ProblemIfNotFound: true);
				if (string.IsNullOrEmpty(InputFile))
				{
					Console.WriteLine("We have no InputFile. Bailing.");
					return;
				}
			}

			if (OutputObjectFileName.Contains(".ispc.generated.dummy.h"))
			{
				var ISPCOutputObjectFileName = OutputObjectFileName.Replace(".ispc.generated.dummy.h", ".ispc.generated.h");

				var ISPCAction = string.Format("'Action_{0}'", ActionIndex);
				//ActionList.Add(ISPCAction);
				DependencyActionList.Add(ISPCAction);
				AddText(string.Format("Exec({0})\n{{\n", ISPCAction));
				AddText("\t.ExecExecutable = '$ISPC$'\n");
				AddText(string.Format("\t.ExecInput = '{0}'\n", InputFile));
				AddText(string.Format("\t.ExecOutput = '{0}'\n", ISPCOutputObjectFileName));
				AddText(string.Format("\t.ExecArguments = '{0} {1} {2}  {3}'\n"
					, InputFile, OutFlag, ISPCOutputObjectFileName, OtherCompilerOptions));
				if (DependencyIndices.Count > 0)
				{
					List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
					AddText(string.Format("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(",", DependencyNames.ToArray())));
				}
				//AddText(string.Format("\t.ExecArguments = '{2} {3} {1}  {0}'\n"
				//	, OtherCompilerOptions, OutputObjectFileName, InputFile, OutFlag));
				AddText("}\n");

				var CopyAction = string.Format("'Action_{0}_dummy_h'", ActionIndex);
				//ActionList.Add(CopyAction);
				DependencyActionList.Add(CopyAction);
				AddText(string.Format("Copy({0})\n{{\n", CopyAction));
				AddText(string.Format("\t.Source = '{0}'\n", ISPCOutputObjectFileName));
				AddText(string.Format("\t.Dest = '{0}'\n", OutputObjectFileName));
				AddText(string.Format("\t.PreBuildDependencies = {{ {0} }}\n", ISPCAction));
				AddText("}\n");
			}
			else
			{
				var ISPCAction0 = string.Format("\"Action_{0}\"", ActionIndex);
				var ISPCAction = string.Format("'Action_{0}'", ActionIndex);
				ActionList.Add(ISPCAction);
				AddText(string.Format("Exec({0})\n{{\n", ISPCAction));
				AddText("\t.ExecExecutable = '$ISPC$'\n");
				AddText(string.Format("\t.ExecInput = '{0}'\n", InputFile));
				AddText(string.Format("\t.ExecOutput = '{0}'\n", OutputObjectFileName));
				AddText(string.Format("\t.ExecArguments = '{0} {1} {2}  {3}'\n"
					, InputFile, OutFlag, OutputObjectFileName, OtherCompilerOptions));
				if (DependencyIndices.Count > 0)
				{
					List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
					AddText(string.Format("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(",", DependencyNames.ToArray())));
				}
				//AddText(string.Format("\t.ExecArguments = '{2} {3} {1}  {0}'\n"
				//	, OtherCompilerOptions, OutputObjectFileName, InputFile, OutFlag));
				AddText("}\n");

			}
		}

		private void AddCompileAction(List<Action> InActions, int ActionIndex, List<int> DependencyIndices)
		{
			Action Action = InActions[ActionIndex];
			string CompilerName = GetCompilerName();
			if (Action.CommandPath.FullName.Contains("rc.exe"))
			{
				CompilerName = "UE4ResourceCompiler";
				AddResourceCompileAction(Action, ActionIndex, DependencyIndices);
				return;
			}
			else if (Action.CommandPath.FullName.Contains("ispc.exe"))
			{
				AddISPCCompileAction(Action, ActionIndex, DependencyIndices);
				return;
			}
			else if (Action.CommandArguments.Contains("VisualStudioDTE\\dte80a.cpp"))
			{
				AddGenerateDte80aTLBAction(Action, ActionIndex, DependencyIndices);
				return;
			}

			string[] SpecialCompilerOptions = { "/Fo", "/fo", "/Yc", "/Yu", "/Fp", "-o" };
			var ParsedCompilerOptions = ParseCommandLineOptions(Action.CommandArguments, SpecialCompilerOptions);

			string OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, IsMSVC() ? "/Fo" : "-o", Action, ProblemIfNotFound: !IsMSVC());

			if (IsMSVC() && string.IsNullOrEmpty(OutputObjectFileName)) // Didn't find /Fo, try /fo
			{
				OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, "/fo", Action, ProblemIfNotFound: true);
			}

			if (string.IsNullOrEmpty(OutputObjectFileName)) //No /Fo or /fo, we're probably in trouble.
			{
				Console.WriteLine("We have no OutputObjectFileName. Bailing.");
				return;
			}

			string IntermediatePath = Path.GetDirectoryName(OutputObjectFileName);
			if (string.IsNullOrEmpty(IntermediatePath))
			{
				Console.WriteLine("We have no IntermediatePath. Bailing.");
				Console.WriteLine("Our Action.CommandArguments were: " + Action.CommandArguments);
				return;
			}

			string InputFile = GetOptionValue(ParsedCompilerOptions, "InputFile", Action, ProblemIfNotFound: true);
			if (string.IsNullOrEmpty(InputFile))
			{
				Console.WriteLine("We have no InputFile. Bailing.");
				return;
			}
			var InputFileExtName = Path.GetExtension(InputFile);

			string OtherCompilerOptions = GetOptionValue(ParsedCompilerOptions, "OtherOptions", Action);
			OtherCompilerOptions = OtherCompilerOptions.Replace("we4668", "wd4668");
			OtherCompilerOptions = OtherCompilerOptions.Replace("we4459", "wd4459");

			//string PreprocessOtherCompilerOptions = OtherCompilerOptions.Replace("/c", "/EP");
			//var DependencyFileName = Path.GetDirectoryName(OutputObjectFileName) + "\\" + Path.GetFileNameWithoutExtension(OutputObjectFileName) + ".txt";
			//var PreprocessedFileName = Path.GetDirectoryName(OutputObjectFileName) + "\\" + Path.GetFileNameWithoutExtension(OutputObjectFileName) + ".i";
			//var DependencyAction = string.Format("'Action_{0}_depend'", ActionIndex);
			//DependencyActionList.Add(DependencyAction);
			//AddText(string.Format("Exec({0})\n{{\n", DependencyAction));
			//AddText(string.Format("\t.ExecExecutable = '{0}' \n", Action.CommandPath));
			//AddText(string.Format("\t.ExecInput = \"{0}\"\n", InputFile));
			//AddText(string.Format("\t.ExecOutput = \"{0}\"\n", DependencyFileName));
			//AddText(string.Format("\t.ExecArguments ='-dependencies=\"{0}\" -compiler=\"{1}\" -- \"{1}\" \"%1\" {2} /showIncludes '\n", DependencyFileName, VCEnv.CompilerPath, PreprocessOtherCompilerOptions));
			//if (DependencyIndices.Count > 0)
			//{
			//	List<Action> DependencyActions = DependencyIndices.ConvertAll(x => InActions[x]);
			//	List<Action> ISPCActions = DependencyActions.FindAll(x => x.CommandArguments.Contains(".ispc.generated.h"));
			//	List<Action> ISPCDependencyActions = new List<Action>();
			//	ISPCActions.ForEach(x => ISPCDependencyActions.AddRange(x.PrerequisiteActions));
			//	if (ISPCDependencyActions.Count() > 0)
			//	{
			//		var ISPCDependencyIndices = ISPCDependencyActions.ConvertAll(x => InActions.IndexOf(x));
			//		List<string> DependencyNames = ISPCDependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
			//		AddText(string.Format("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(", ", DependencyNames.ToArray())));
			//	}
			//}
			//AddText("}\n");

			var action = string.Format("'Action_{0}'", ActionIndex);
			ActionList.Add(action);
			AddText(string.Format("ObjectList({0})\n{{\n", action));
			AddText(string.Format("\t.Compiler = '{0}' \n", CompilerName));
			AddText(string.Format("\t.CompilerInputFiles = \"{0}\"\n", InputFile));
			AddText(string.Format("\t.CompilerOutputPath = \"{0}\"\n", IntermediatePath));


			bool bSkipDistribution = false;
			foreach (var it in ForceLocalCompileModules)
			{
				if (Path.GetFullPath(InputFile).Contains(it))
				{
					bSkipDistribution = true;
					break;
				}
			}


			if (!Action.bCanExecuteRemotely || !Action.bCanExecuteRemotelyWithSNDBS || bSkipDistribution)
			{
				AddText(string.Format("\t.AllowDistribution = false\n"));
			}

			string CompilerOutputExtension = ".unset";

			string InputFileExt = ".cpp";
			if (InputFileExtName.ToLower() == ".cc")
			{
				InputFileExt = ".cc";
			}
			else if (InputFileExtName.ToLower() == ".c")
			{
				InputFileExt = ".c";
			}
			else
			{
				InputFileExt = ".cpp";
			}

			if (ParsedCompilerOptions.ContainsKey("/Yc")) //Create PCH
			{
				string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yc", Action, ProblemIfNotFound: true);
				string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);

				AddText(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /Fp\"{0}\" /Yu\"{1}\" {2} '\n", PCHOutputFile, PCHIncludeHeader, OtherCompilerOptions));

				AddText(string.Format("\t.PCHOptions = '\"%1\" /Fp\"%2\" /Yc\"{0}\" {1} /Fo\"{2}\"'\n", PCHIncludeHeader, OtherCompilerOptions, OutputObjectFileName));
				AddText(string.Format("\t.PCHInputFile = \"{0}\"\n", InputFile));
				AddText(string.Format("\t.PCHOutputFile = \"{0}\"\n", PCHOutputFile));
				CompilerOutputExtension = ".obj";
			}
			else if (ParsedCompilerOptions.ContainsKey("/Yu")) //Use PCH
			{
				string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yu", Action, ProblemIfNotFound: true);
				string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);
				string PCHToForceInclude = PCHOutputFile.Replace(".pch", "");
				AddText(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /Fp\"{0}\" /Yu\"{1}\" /FI\"{2}\" {3} '\n", PCHOutputFile, PCHIncludeHeader, PCHToForceInclude, OtherCompilerOptions));
				CompilerOutputExtension = InputFileExt + ".obj";
			}
			else
			{
				if (IsMSVC())
				{
					AddText(string.Format("\t.CompilerOptions = '{0} /Fo\"%2\" \"%1\" '\n", OtherCompilerOptions));
					CompilerOutputExtension = InputFileExt + ".obj";
				}
				else
				{
					AddText(string.Format("\t.CompilerOptions = '{0} -o \"%2\" \"%1\" '\n", OtherCompilerOptions));
					CompilerOutputExtension = InputFileExt + ".o";
				}
			}

			AddText(string.Format("\t.CompilerOutputExtension = '{0}' \n", CompilerOutputExtension));

			List<string> DependencyNameList = new List<string>();
			foreach (var LibAction in Action.PrerequisiteActions)
			{
				if (LibAction == null)
				{
					continue;
				}
				if (LibAction.CommandPath.FullName.Contains("cl-filter.exe"))
				{
					var LibActionIndex = InActions.IndexOf(LibAction);
					string ActionName = string.Format("'Action_{0}'", LibActionIndex);
					DependencyNameList.Add(ActionName);
				}
				else if (LibAction.CommandDescription == "Copy" && LibAction.StatusDescription.Contains("ispc.generated.h"))
				{
					foreach (var ISPCAction in LibAction.PrerequisiteActions)
					{
						var ISPCActionIndex = InActions.IndexOf(ISPCAction);
						string ActionName = string.Format("'Action_{0}'", ISPCActionIndex);
						DependencyNameList.Add(ActionName);
					}
				}
				else if (LibAction.StatusDescription.Contains("dte80a.tlh"))
				{
					foreach (var DTEAction in LibAction.PrerequisiteActions)
					{
						var DTEActionIndex = InActions.IndexOf(DTEAction);
						string ActionName = string.Format("'Action_{0}'", DTEActionIndex);
						DependencyNameList.Add(ActionName);
					}
				}

				//if (DependencyIndices.Count > 0)
				//{
				//	List<Action> DependencyActions = DependencyIndices.ConvertAll(x => InActions[x]);
				//	List<Action> ISPCActions = DependencyActions.FindAll(x => x.CommandArguments.Contains(".ispc.generated.h"));
				//	List<Action> ISPCDependencyActions = new List<Action>();
				//	ISPCActions.ForEach(x => ISPCDependencyActions.AddRange(x.PrerequisiteActions));
				//	if (ISPCDependencyActions.Count() > 0)
				//	{
				//		var ISPCDependencyIndices = ISPCDependencyActions.ConvertAll(x => InActions.IndexOf(x));
				//		List<string> DependencyNames = ISPCDependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
				//		AddText(string.Format("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(", ", DependencyNames.ToArray())));
				//	}
				//}

			}
			AddText(string.Format("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(",", DependencyNameList.ToArray())));

			AddText(string.Format("}}\n\n"));
		}

		private void AddResourceCompileAction(Action Action, int ActionIndex, List<int> DependencyIndices)
		{
			string CompilerName = "UE4ResourceCompiler";

			string[] SpecialCompilerOptions = { "/Fo", "/fo", "/Yc", "/Yu", "/Fp", "-o" };
			var ParsedCompilerOptions = ParseCommandLineOptions(Action.CommandArguments, SpecialCompilerOptions);

			string OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, IsMSVC() ? "/Fo" : "-o", Action, ProblemIfNotFound: !IsMSVC());

			if (IsMSVC() && string.IsNullOrEmpty(OutputObjectFileName)) // Didn't find /Fo, try /fo
			{
				OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, "/fo", Action, ProblemIfNotFound: true);
			}

			if (string.IsNullOrEmpty(OutputObjectFileName)) //No /Fo or /fo, we're probably in trouble.
			{
				Console.WriteLine("We have no OutputObjectFileName. Bailing.");
				return;
			}

			string IntermediatePath = Path.GetDirectoryName(OutputObjectFileName);
			if (string.IsNullOrEmpty(IntermediatePath))
			{
				Console.WriteLine("We have no IntermediatePath. Bailing.");
				Console.WriteLine("Our Action.CommandArguments were: " + Action.CommandArguments);
				return;
			}

			string InputFile = GetOptionValue(ParsedCompilerOptions, "InputFile", Action, ProblemIfNotFound: true);
			if (string.IsNullOrEmpty(InputFile))
			{
				Console.WriteLine("We have no InputFile. Bailing.");
				return;
			}
			var InputFileExtName = Path.GetExtension(InputFile);

			var action = string.Format("'Action_{0}'", ActionIndex);
			ActionList.Add(action);
			AddText(string.Format("ObjectList({0})\n{{\n", action));
			AddText(string.Format("\t.Compiler = '{0}' \n", CompilerName));
			AddText(string.Format("\t.CompilerInputFiles = \"{0}\"\n", InputFile));
			AddText(string.Format("\t.CompilerOutputPath = \"{0}\"\n", IntermediatePath));


			bool bSkipDistribution = false;
			foreach (var it in ForceLocalCompileModules)
			{
				if (Path.GetFullPath(InputFile).Contains(it))
				{
					bSkipDistribution = true;
					break;
				}
			}


			if (!Action.bCanExecuteRemotely || !Action.bCanExecuteRemotelyWithSNDBS || bSkipDistribution)
			{
				AddText(string.Format("\t.AllowDistribution = false\n"));
			}

			string OtherCompilerOptions = GetOptionValue(ParsedCompilerOptions, "OtherOptions", Action);
			OtherCompilerOptions = OtherCompilerOptions.Replace("we4668", "wd4668");
			OtherCompilerOptions = OtherCompilerOptions.Replace("we4459", "wd4459");
			string CompilerOutputExtension = ".unset";

			AddText(string.Format("\t.CompilerOptions = '{0} /fo\"%2\" \"%1\" '\n", OtherCompilerOptions));
			CompilerOutputExtension = Path.GetExtension(InputFile) + ".res";

			AddText(string.Format("\t.CompilerOutputExtension = '{0}' \n", CompilerOutputExtension));

			if (DependencyIndices.Count > 0)
			{
				List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
				AddText(string.Format("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(",", DependencyNames.ToArray())));
			}

			AddText(string.Format("}}\n\n"));
		}

		private void AddLinkAction(List<Action> Actions, int ActionIndex, List<int> DependencyIndices)
		{
			Action Action = Actions[ActionIndex];
			string[] SpecialLinkerOptions = { "/OUT:", "@", "-o" };
			var ParsedLinkerOptions = ParseCommandLineOptions(Action.CommandArguments, SpecialLinkerOptions, SaveResponseFile: true, SkipInputFile: Action.CommandPath.FullName.Contains("orbis-clang"));

			string OutputFile;

			if (IsXBOnePDBUtil(Action))
			{
				OutputFile = ParsedLinkerOptions["OtherOptions"].Trim(' ').Trim('"');
			}
			else if (IsMSVC())
			{
				OutputFile = GetOptionValue(ParsedLinkerOptions, "/OUT:", Action, ProblemIfNotFound: true);
			}
			else //PS4
			{
				OutputFile = GetOptionValue(ParsedLinkerOptions, "-o", Action, ProblemIfNotFound: false);
				if (string.IsNullOrEmpty(OutputFile))
				{
					OutputFile = GetOptionValue(ParsedLinkerOptions, "InputFile", Action, ProblemIfNotFound: true);
				}
			}

			if (string.IsNullOrEmpty(OutputFile))
			{
				Console.WriteLine("Failed to find output file. Bailing.");
				return;
			}

			string ResponseFilePath = GetOptionValue(ParsedLinkerOptions, "@", Action);
			string OtherCompilerOptions = GetOptionValue(ParsedLinkerOptions, "OtherOptions", Action);

			List<int> PrebuildDependencies = new List<int>();

			if (IsXBOnePDBUtil(Action))
			{
				var action = string.Format("'Action_{0}'", ActionIndex);
				ActionList.Add(action);
				AddText(string.Format("Exec({0})\n{{\n", action));
				AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
				AddText(string.Format("\t.ExecArguments = '{0}'\n", Action.CommandArguments));
				AddText(string.Format("\t.ExecInput = {{ {0} }} \n", ParsedLinkerOptions["InputFile"]));
				AddText(string.Format("\t.ExecOutput = '{0}' \n", OutputFile));
				AddText(string.Format("\t.PreBuildDependencies = {{ {0} }} \n", ParsedLinkerOptions["InputFile"]));
				AddText(string.Format("}}\n\n"));
			}
			else if (IsPS4SymbolTool(Action))
			{
				string searchString = "-map=\"";
				int execArgumentStart = Action.CommandArguments.LastIndexOf(searchString) + searchString.Length;
				int execArgumentEnd = Action.CommandArguments.IndexOf("\"", execArgumentStart);
				string ExecOutput = Action.CommandArguments.Substring(execArgumentStart, execArgumentEnd - execArgumentStart);

				var action = string.Format("'Action_{0}'", ActionIndex);
				ActionList.Add(action);
				AddText(string.Format("Exec({0})\n{{\n", action));
				AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
				AddText(string.Format("\t.ExecArguments = '{0}'\n", Action.CommandArguments));
				AddText(string.Format("\t.ExecOutput = '{0}'\n", ExecOutput));
				AddText(string.Format("\t.PreBuildDependencies = {{ 'Action_{0}' }} \n", ActionIndex - 1));
				AddText(string.Format("}}\n\n"));
			}
			else if (Action.CommandPath.FullName.Contains("lib.exe") || Action.CommandPath.FullName.Contains("orbis-snarl"))
			{
				if (DependencyIndices.Count > 0)
				{
					for (int i = 0; i < DependencyIndices.Count; ++i) //Don't specify pch or resource files, they have the wrong name and the response file will have them anyways.
					{
						int depIndex = DependencyIndices[i];
						foreach (FileItem item in Actions[depIndex].ProducedItems)
						{
							if (item.ToString().Contains(".pch") || item.ToString().Contains(".res"))
							{
								DependencyIndices.RemoveAt(i);
								i--;
								PrebuildDependencies.Add(depIndex);
								break;
							}
						}
					}
				}

				var action = string.Format("'Action_{0}'", ActionIndex);
				ActionList.Add(action);
				AddText(string.Format("Library({0})\n{{\n", action));
				AddText(string.Format("\t.Compiler = '{0}'\n", GetCompilerName()));
				if (IsMSVC())
					AddText(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /c'\n"));
				else
					AddText(string.Format("\t.CompilerOptions = '\"%1\" -o \"%2\" -c'\n"));
				AddText(string.Format("\t.CompilerOutputPath = \"{0}\"\n", Path.GetDirectoryName(OutputFile)));
				AddText(string.Format("\t.Librarian = '{0}' \n", Action.CommandPath));

				if (!string.IsNullOrEmpty(ResponseFilePath))
				{
					if (IsMSVC())
						// /ignore:4042 to turn off the linker warning about the output option being present twice (command-line + rsp file)
						AddText(string.Format("\t.LibrarianOptions = ' /OUT:\"%2\" /ignore:4042 @\"{0}\" \"%1\"' \n", ResponseFilePath));
					else if (IsPS4())
						AddText(string.Format("\t.LibrarianOptions = '\"%2\" @\"%1\"' \n", ResponseFilePath));
					else
						AddText(string.Format("\t.LibrarianOptions = '\"%2\" @\"%1\" {0}' \n", OtherCompilerOptions));
				}
				else
				{
					if (IsMSVC())
						AddText(string.Format("\t.LibrarianOptions = ' /OUT:\"%2\" {0} \"%1\"' \n", OtherCompilerOptions));
				}

				if (DependencyIndices.Count > 0)
				{
					List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));

					if (IsPS4())
						AddText(string.Format("\t.LibrarianAdditionalInputs = {{ '{0}' }} \n", ResponseFilePath)); // Hack...Because FastBuild needs at least one Input file
					else if (!string.IsNullOrEmpty(ResponseFilePath))
						AddText(string.Format("\t.LibrarianAdditionalInputs = {{ {0} }} \n", DependencyNames[0])); // Hack...Because FastBuild needs at least one Input file
					else if (IsMSVC())
						AddText(string.Format("\t.LibrarianAdditionalInputs = {{ {0} }} \n", string.Join(",", DependencyNames.ToArray())));

					PrebuildDependencies.AddRange(DependencyIndices);
				}
				else
				{
					string InputFile = GetOptionValue(ParsedLinkerOptions, "InputFile", Action, ProblemIfNotFound: true);
					if (InputFile != null && InputFile.Length > 0)
						AddText(string.Format("\t.LibrarianAdditionalInputs = {{ '{0}' }} \n", InputFile));
				}

				if (PrebuildDependencies.Count > 0)
				{
					List<string> PrebuildDependencyNames = PrebuildDependencies.ConvertAll(x => string.Format("'Action_{0}'", x));
					AddText(string.Format("\t.PreBuildDependencies = {{ {0} }} \n", string.Join(",", PrebuildDependencyNames.ToArray())));
				}

				AddText(string.Format("\t.LibrarianOutput = '{0}' \n", OutputFile));
				AddText(string.Format("}}\n\n"));
			}
			else if (Action.CommandPath.FullName.Contains("link.exe") || Action.CommandPath.FullName.Contains("link-filter.exe") || Action.CommandPath.FullName.Contains("orbis-clang"))
			{
				FileReference LinkCommandPath = Action.CommandPath;
				if (Action.CommandPath.FullName.Contains("link-filter.exe"))
				{
					LinkCommandPath = VCEnv.LinkerPath;
				}

				if (DependencyIndices.Count > 0) //Insert a dummy node to make sure all of the dependencies are finished.
												 //If FASTBuild supports PreBuildDependencies on the Executable action we can remove this.
				{
					string dummyText = string.IsNullOrEmpty(ResponseFilePath) ? GetOptionValue(ParsedLinkerOptions, "InputFile", Action) : ResponseFilePath;
					File.SetLastAccessTimeUtc(dummyText, DateTime.UtcNow);
					AddText(string.Format("Copy('Action_{0}_dummy')\n{{ \n", ActionIndex));
					AddText(string.Format("\t.Source = '{0}' \n", dummyText));
					AddText(string.Format("\t.Dest = '{0}' \n", dummyText + ".dummy"));
					//List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("\t\t'Action_{0}', ;{1}", x, Actions[x].StatusDescription));
					//AddText(string.Format("\t.PreBuildDependencies = {{\n{0}\n\t}} \n", string.Join("\n", DependencyNames.ToArray())));
					AddText(string.Format("}}\n\n"));
				}

				var action = string.Format("'Action_{0}'", ActionIndex);
				ActionList.Add(action);
				AddText(string.Format("Executable({0})\n{{ \n", action));
				//AddText(string.Format("\t.Linker = '{0}' \n", Action.CommandPath));
				AddText(string.Format("\t.Linker = '{0}' \n", LinkCommandPath));

				if (DependencyIndices.Count == 0)
				{
					AddText(string.Format("\t.Libraries = {{ '{0}' }} \n", ResponseFilePath));
					if (IsMSVC())
					{
						if (BuildType == FBBuildType.XBOne)
						{
							AddText(string.Format("\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" @\"{0}\" {1} ' \n", ResponseFilePath, OtherCompilerOptions)); // The TLBOUT is a huge bodge to consume the %1.
						}
						else
						{
							// /ignore:4042 to turn off the linker warning about the output option being present twice (command-line + rsp file)
							AddText(string.Format("\t.LinkerOptions = '/TLBOUT:\"%1\" /ignore:4042 /Out:\"%2\" @\"{0}\" ' \n", ResponseFilePath)); // The TLBOUT is a huge bodge to consume the %1.
						}
					}
					else
						AddText(string.Format("\t.LinkerOptions = '{0} -o \"%2\" @\"%1\"' \n", OtherCompilerOptions)); // The MQ is a huge bodge to consume the %1.
				}
				else
				{
					AddText(string.Format("\t.Libraries = 'Action_{0}_dummy' \n", ActionIndex));
					if (IsMSVC())
					{
						if (BuildType == FBBuildType.XBOne)
						{
							AddText(string.Format("\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" @\"{0}\" {1} ' \n", ResponseFilePath, OtherCompilerOptions)); // The TLBOUT is a huge bodge to consume the %1.
						}
						else
						{
							AddText(string.Format("\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" @\"{0}\" ' \n", ResponseFilePath)); // The TLBOUT is a huge bodge to consume the %1.
						}
					}
					else
						AddText(string.Format("\t.LinkerOptions = '{0} -o \"%2\" @\"%1\"' \n", OtherCompilerOptions)); // The MQ is a huge bodge to consume the %1.
				}

				{
					List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("\t\t'Action_{0}', ;{1}", x, Actions[x].StatusDescription));
					DependencyNames.Insert(0, String.Format("\t\t'Action_{0}_dummy', ", ActionIndex));
					AddText(string.Format("\t.PreBuildDependencies = {{\n{0}\n\t}} \n", string.Join("\n", DependencyNames.ToArray())));
				}

				AddText(string.Format("\t.LinkerOutput = '{0}' \n", OutputFile));
				AddText(string.Format("}}\n\n"));
			}
		}

		private void AddCopyAction(Action Action, int ActionIndex, List<int> DependencyIndices, List<Action> LocalExecutorActions)
		{
			bool isContainDTE80a_tlh = Action.CommandArguments.Contains("VisualStudioDTE\\dte80a.tlh");
			bool isContainISPC_tlh = Action.CommandArguments.Contains(".ispc.generated.h");

			if (Action.CommandArguments.Contains("copy") && !isContainDTE80a_tlh && !isContainISPC_tlh)
			{
				string[] tokens = Action.CommandArguments.Split(' ');
				char[] flag2 = { ' ', '\\', '/', '\"', '@', '+', ',' };
				string SourceFile = tokens[3].TrimStart(flag2).TrimEnd(flag2);
				string DestFile = tokens[4].TrimStart(flag2).TrimEnd(flag2);

				//if (SourceFile != DestFile)
				//{
				//	var action = string.Format("'Action_{0}'", ActionIndex);
				//	ActionList.Add(action);
				//	AddText(string.Format("Exec({0})\n{{\n", action));
				//	AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
				//	AddText(string.Format("\t.ExecInput = '{0}'\n", SourceFile));
				//	AddText(string.Format("\t.ExecOutput = '{0}'\n", DestFile));
				//	AddText(string.Format("\t.ExecArguments = ' {0} '\n", Action.CommandArguments));
				//	if (DependencyIndices.Count > 0)
				//	{
				//		List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
				//		AddText(string.Format("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(", ", DependencyNames.ToArray())));
				//	}
				//	AddText("}\n");
				//}
				//else
				{
					var action = string.Format("'Action_{0}'", ActionIndex);
					ActionList.Add(action);
					AddText(string.Format("Exec({0})\n{{\n", action));
					AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
					AddText(string.Format("\t.ExecOutput = '{0}'\n", DestFile));
					AddText(string.Format("\t.ExecArguments = ' {0} '\n", Action.CommandArguments));
					if (DependencyIndices.Count > 0)
					{
						List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
						AddText(string.Format("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(", ", DependencyNames.ToArray())));
					}
					AddText("}\n");
				}
			}
			else
			{
				LocalExecutorActions.Add(Action);
			}
		}

		private void AddGenerateDte80aTLBAction(Action Action, int ActionIndex, List<int> DependencyIndices)
		{
			string[] SpecialCompilerOptions = { "/Fo", "/fo", "/Yc", "/Yu", "/Fp", "-o" };
			var ParsedCompilerOptions = ParseCommandLineOptions(Action.CommandArguments, SpecialCompilerOptions);

			string InputFile = GetOptionValue(ParsedCompilerOptions, "InputFile", Action, ProblemIfNotFound: true);
			if (string.IsNullOrEmpty(InputFile))
			{
				Console.WriteLine("We have no InputFile. Bailing.");
				return;
			}

			string TargetFile = InputFile.Replace("dte80a.cpp", "dte80a.tlh");

			var action = string.Format("'Action_{0}'", ActionIndex);
			ActionList.Add(action);
			AddText(string.Format("Exec({0})\n{{\n", action));
			AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
			AddText(string.Format("\t.ExecInput = '{0}'\n", InputFile));
			AddText(string.Format("\t.ExecOutput = '{0}'\n", TargetFile));
			AddText(string.Format("\t.ExecArguments = ' {0} '\n", Action.CommandArguments));
			if (Action.PrerequisiteActions.Count() > 0)
			{
				List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("\t\t'Action_{0}', ", x));
				AddText(string.Format("\t.PreBuildDependencies = {{\n{0}\n\t}} \n", string.Join("\n", DependencyNames.ToArray())));
			}
			AddText("}\n");
		}

		private FileStream bffOutputFileStream = null;
		private List<String> ActionList;
		private List<String> DependencyActionList;

		private List<Action> FixDependency(List<Action> InActions)
		{
			// PCH action has dependency on ISPC actions, when module has ISPC actions
			var ISPCActions = InActions.Where(Action => Action.CommandPath.FullName.Contains("ispc.exe"));
			var PCHActions = InActions.Where(Action => Action.CommandArguments.Contains(".h.pch.response"));
			foreach (Action Action in InActions)
			{
				var PCHActionList = Action.PrerequisiteActions.Intersect(PCHActions);
				if (PCHActionList.Count() > 0)
				{
					var ISPCActionList = Action.PrerequisiteActions.Intersect(ISPCActions);
					if (ISPCActionList.Count() > 0)
					{
						foreach (Action PCHAction in PCHActionList)
						{
							foreach (Action ISPCAction in ISPCActionList)
							{
								PCHAction.PrerequisiteActions.Add(ISPCAction);
							}
						}
					}
				}
			}

			var DTE80Action = InActions.Find(Action => Action.CommandArguments.Contains("dte80a.cpp"));
			var VisualStudioCodeSourceCodeAccessActions = InActions.FindAll(Action => Action.CommandArguments.Contains("Module.VisualStudioCodeSourceCodeAccess.cpp.obj.response"));
			foreach (Action Action in VisualStudioCodeSourceCodeAccessActions)
			{
				Action.PrerequisiteActions.Add(DTE80Action);
			}
			return InActions;
		}
		private bool CreateBffFile(List<Action> InActions, string BffFilePath, List<Action> LocalExecutorActions)
		{

			List<Action> Actions = SortActions(FixDependency(InActions));

			ActionList = new List<String>();
			DependencyActionList = new List<String>();

			try
			{
				bffOutputFileStream = new FileStream(BffFilePath, FileMode.Create, FileAccess.Write);

				WriteEnvironmentSetup(); //Compiler, environment variables and base paths

				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					Action Action = Actions[ActionIndex];

					// Resolve dependencies
					List<int> DependencyIndices = new List<int>();
					foreach (Action PrerequisiteAction in Action.PrerequisiteActions)
					{
						int ProducingActionIndex = Actions.IndexOf(PrerequisiteAction);
						if (ProducingActionIndex >= 0)
						{
							DependencyIndices.Add(ProducingActionIndex);
						}
					}

					AddText(string.Format("// \"{0}\" {1}\n", Action.CommandPath, Action.CommandArguments));
					switch (Action.ActionType)
					{
						case ActionType.Compile: AddCompileAction(Actions, ActionIndex, DependencyIndices); break;
						case ActionType.Link: AddLinkAction(Actions, ActionIndex, DependencyIndices); break;
						case ActionType.WriteMetadata: LocalExecutorActions.Add(Action); break;
						case ActionType.BuildProject: AddCopyAction(Action, ActionIndex, DependencyIndices, LocalExecutorActions); break;
						default: Console.WriteLine("Fastbuild is ignoring an unsupported action: " + Action.ActionType.ToString()); break;
					}
				}

				if (ActionList.Count() > 0 || DependencyActionList.Count() > 0)
				{
					ActionList.InsertRange(0, DependencyActionList);

					AddText("Alias( 'all' ) \n{\n");
					AddText("\t.Targets = { \n");
					List<string> DependencyNames = ActionList.ConvertAll(x => string.Format("\t\t{0}", x));
					AddText(string.Format("\n{0}\n\t\n", string.Join(", \n", DependencyNames.ToArray())));
					AddText("\n\t}\n");
					AddText("}\n");
				}
				bffOutputFileStream.Close();
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception while creating bff file: " + e.ToString());
				return false;
			}

			return true;
		}

		private bool ExecuteBffFile(string BffFilePath)
		{
			string cacheArgument = "";

			if (bEnableCaching)
			{
				switch (CacheMode)
				{
					case eCacheMode.ReadOnly:
						cacheArgument = "-cacheread";
						break;
					case eCacheMode.WriteOnly:
						cacheArgument = "-cachewrite";
						break;
					case eCacheMode.ReadWrite:
						cacheArgument = "-cache";
						break;
				}
			}

			string distArgument = bEnableDistribution ? "-distverbose" : "";
			string cleanArgument = bCleanBuild ? "-clean": "";

			//Interesting flags for FASTBuild: -nostoponerror, -verbose, -monitor (if FASTBuild Monitor Visual Studio Extension is installed!)
			// Yassine: The -clean is to bypass the FastBuild internal dependencies checks (cached in the fdb) as it could create some conflicts with UBT.
			//			Basically we want FB to stupidly compile what UBT tells it to.
			//string FBCommandLine = string.Format("-monitor -summary {0} {1} -ide -clean -config {2}", distArgument, cacheArgument, BffFilePath);
			string FBCommandLine = string.Format("-monitor -summary {0} {1} -progress -ide {2} -config {3}", distArgument, cacheArgument, cleanArgument, BffFilePath);

			ProcessStartInfo FBStartInfo = new ProcessStartInfo(string.IsNullOrEmpty(FBuildExePathOverride) ? "fbuild" : FBuildExePathOverride, FBCommandLine);

			FBStartInfo.UseShellExecute = false;
			FBStartInfo.WorkingDirectory = Path.Combine(UnrealBuildTool.EngineDirectory.MakeRelativeTo(DirectoryReference.GetCurrentDirectory()), "Source");

			try
			{
				Process FBProcess = new Process();
				FBProcess.StartInfo = FBStartInfo;

				FBStartInfo.RedirectStandardError = true;
				FBStartInfo.RedirectStandardOutput = true;
				FBProcess.EnableRaisingEvents = true;

				DataReceivedEventHandler OutputEventHandler = (Sender, Args) =>
				{
					if (Args.Data != null)
						Console.WriteLine(Args.Data);
				};

				FBProcess.OutputDataReceived += OutputEventHandler;
				FBProcess.ErrorDataReceived += OutputEventHandler;

				FBProcess.Start();

				FBProcess.BeginOutputReadLine();
				FBProcess.BeginErrorReadLine();

				FBProcess.WaitForExit();
				return FBProcess.ExitCode == 0;
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception launching fbuild process. Is it in your path?" + e.ToString());
				return false;
			}
		}
	}
}
