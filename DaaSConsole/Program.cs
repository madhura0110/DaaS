//-----------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DaaS.Configuration;
using DaaS.Diagnostics;
using DaaS.HeartBeats;
using DaaS.Sessions;
using DaaS;
using Newtonsoft.Json;

namespace ConsoleTester
{
    class Program
    {
        static SessionController SessionController = new SessionController();

        enum Options
        {
            CollectLogs,
            Troubleshoot,
            AnalyzeSession,
            CancelSession,
            CollectKillAnalyze,
            ListSessions,
            ListDiagnosers,
            GetSasUri,
            SetSasUri,
            Setup,
            GetSetting,
            SetSetting,
            ListInstances,
            Help,
            AllInstances,
            BlobSasUri
        }

        private class Argument
        {
            public Options Command;
            public String Usage;
            public String Description;
        }

        static Settings settings = new Settings();

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                ShowUsage();
                return;
            }

            int argNum = 0;

            while (argNum < args.Length)
            {
                Options option;
                var currentArgument = args[argNum];
                if (!ArguementIsAParameter(currentArgument))
                {
                    ShowUsage();
                    return;
                }

                if (!Options.TryParse(args[argNum].Substring(1, args[argNum].Length - 1), true, out option))
                {
                    ShowUsage();
                    return;
                }

                argNum++;

                switch (option)
                {
                    case (Options.ListSessions):
                        var allSessions = SessionController.GetAllSessions();
                        Console.WriteLine("All Sessions:");
                        foreach (var session in allSessions)
                        {
                            Console.WriteLine("  Session log path: " + session.FullPermanentStoragePath);
                        }
                        break;
                    case (Options.ListDiagnosers):
                        {
                            var diagnosers = SessionController.GetAllDiagnosers().ToList();
                            ListDiagnosers(diagnosers);
                            break;
                        }
                    case (Options.AnalyzeSession):
                        {
                            Logger.LogVerboseEvent($"DaasConsole AnalyzeSession started with {string.Join(" ", args)} parameters");
                            argNum = ModifySession(args, argNum, SessionController.Analyze);
                            break;
                        }
                    case (Options.CancelSession):
                        {
                            Logger.LogVerboseEvent($"DaasConsole CancelSession started with {string.Join(" ", args)} parameters");
                            argNum = ModifySession(args, argNum, SessionController.Cancel);
                            break;
                        }
                    case (Options.Setup):
                        {
                            SessionController.StartSessionRunner(sourceDir: ".", extraFilesToCopy: new List<string>() { @"Configuration\DiagnosticSettings.xml" });
                            break;
                        }
                    case (Options.Help):
                        ShowUsage();
                        break;
                    case (Options.CollectLogs):
                    case (Options.Troubleshoot):
                    case (Options.CollectKillAnalyze):
                        {
                            CollectLogsAndTakeActions(option, args, ref argNum);
                            break;
                        }
                    case (Options.GetSasUri):
                        Console.WriteLine("Sas Uri:");
                        Console.WriteLine(SessionController.BlobStorageSasUri);
                        break;
                    case (Options.SetSasUri):
                        string uri = args[argNum];
                        argNum++;
                        SessionController.BlobStorageSasUri = uri;
                        Console.WriteLine("Sas Uri is now:");
                        Console.WriteLine(SessionController.BlobStorageSasUri);
                        break;
                    case (Options.GetSetting):
                        {
                            string settingName;
                            try
                            {
                                settingName = args[argNum];
                                argNum++;
                            }
                            catch
                            {
                                Console.WriteLine("GetSetting options not correctly specified");
                                ShowUsage();
                                return;
                            }
                            var settingValue = settings.GetSetting(settingName);
                            Console.WriteLine("Got {0} = {1}", settingName, settingValue);
                            break;
                        }
                    case (Options.SetSetting):
                        {
                            string settingName;
                            string settingValue;
                            try
                            {
                                settingName = args[argNum];
                                argNum++;
                                settingValue = args[argNum];
                                argNum++;
                            }
                            catch
                            {
                                Console.WriteLine("SetSetting options not correctly specified");
                                ShowUsage();
                                return;
                            }
                            settings.SaveSetting(settingName, settingValue);
                            settingValue = settings.GetSetting(settingName);
                            Console.WriteLine("Set {0} = {1}", settingName, settingValue);
                            break;
                        }
                    case (Options.ListInstances):
                        {
                            Console.WriteLine("Current Instances:");
                            foreach (var instance in HeartBeatController.GetLiveInstances())
                            {
                                Console.WriteLine(instance);
                            }
                            break;
                        }
                    default:
                        break;
                }

                Console.WriteLine();
            }
        }

        private static int ModifySession(string[] args, int argNum, Func<Session, Session> modifySessionFunc)
        {
            string sessionId;
            try
            {
                sessionId = args[argNum];
            }
            catch (Exception)
            {
                Console.WriteLine("Session Id not specified");
                return argNum;
            }
            argNum++;
            try
            {
                var session = SessionController.GetSessionWithId(new SessionId(sessionId));
                modifySessionFunc(session);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
            return argNum;
        }

        private static void CollectLogsAndTakeActions(Options options, string[] args, ref int argNum)
        {
            try
            {
                var diagnosersToRun = GetDiagnosersToRun(args, ref argNum);
                TimeSpan timeToRunFor = GetTimeSpanFromArg(args, ref argNum);
                string blobSasUri = GetBlobSasUriFromArg(args, ref argNum);
                bool runOnAllInstances = GetRunAllInstancesFromArg(args, ref argNum);

                var currentInstance = Instance.GetCurrentInstance();

                List<Instance> Instances = new List<Instance>();
                if (!runOnAllInstances)
                {
                    Instances.Add(currentInstance);
                    Console.WriteLine($"Running Diagnosers on {currentInstance.Name} instance only");
                }
                else
                {
                    // An empty diagnoser list means runs DaaS on all instances.
                    Console.WriteLine("Running Diagnosers on all instances");
                }

                Console.WriteLine($"BlobSaUri for the session is '{blobSasUri}'");

                if (!string.IsNullOrWhiteSpace(blobSasUri))
                {
                    if (!DaaS.Storage.BlobController.ValidateBlobSasUri(blobSasUri, out Exception exStorage))
                    {
                        throw new ApplicationException($"BlobSasUri specified is invalid. Failed with error - {exStorage.Message}");
                    }
                }

                var details = new
                {
                    Diagnoser = string.Join(",", diagnosersToRun.Select(x => x.Name)),
                    TimeSpanToRunFor = timeToRunFor.TotalSeconds.ToString("0"),
                    HasBlobSasUri = !string.IsNullOrWhiteSpace(blobSasUri),
                    AllInstances = runOnAllInstances,
                    InstancesSelected = string.Join(",", Instances.Select(x => x.Name)),
                    Options = options.ToString()
                };

                var detailsString = JsonConvert.SerializeObject(details);
                Logger.LogDaasConsoleEvent("DaasConsole started a new Session", detailsString);
                EventLog.WriteEntry("Application", $"DaasConsole started with {detailsString} ", EventLogEntryType.Information);

                // Collect the logs for just the current instance
                Console.WriteLine("Starting data collection");
                Session session = null;

                if (options == Options.CollectKillAnalyze || options == Options.CollectLogs)
                {
                    session = SessionController.CollectLiveDataLogs(
                        timeToRunFor,
                        diagnosersToRun,
                        true,
                        Instances, null, blobSasUri);
                }
                else if (options == Options.Troubleshoot)
                {
                    session = SessionController.TroubleshootLiveData(
                        timeToRunFor,
                        diagnosersToRun,
                        true, Instances, null, blobSasUri);
                }
                Console.WriteLine("Waiting for collection to complete...");

                Console.WriteLine($"Going to sleep for {timeToRunFor} seconds");
                Thread.Sleep(timeToRunFor);
                Console.WriteLine("done sleeping");

                do
                {
                    Thread.Sleep(TimeSpan.FromSeconds(10));
                    Console.Write(".");
                    session = SessionController.GetSessionWithId(session.SessionId);
                } while (session.Status == SessionStatus.Active);
                Console.WriteLine("Completed");

                if (options == Options.CollectKillAnalyze)
                {
                    Console.WriteLine("Analyzing collected logs");
                    // Say we want the logs analyzed
                    SessionController.Analyze(session);

                    // Kill the regular site's w3wp process.  The logs can be analyzed by another instance or by this same instance when it is in a healtheir state
                    Process mainSiteW3wpProcess = GetMainSiteW3wpProcess();
                    Console.WriteLine("Killing process {0} with pid {1}", mainSiteW3wpProcess.ProcessName, mainSiteW3wpProcess.Id);
                    mainSiteW3wpProcess.Kill();
                    string sessionId = session != null ? session.SessionId.ToString() : string.Empty;
                    Logger.LogSessionVerboseEvent($"DaasConsole killed process {mainSiteW3wpProcess.ProcessName} with pid {mainSiteW3wpProcess.Id}", sessionId);
                }
            }
            catch (Exception ex)
            {
                string logMessage = $"Unhandled exception in DaasConsole.exe - {ex} ";
                EventLog.WriteEntry("Application", logMessage, EventLogEntryType.Information);
                Console.WriteLine(logMessage);
                Logger.LogErrorEvent("Unhandled exception in DaasConsole.exe while collecting logs and taking actions", ex);
            }
        }

        private static string GetBlobSasUriFromArg(string[] args, ref int argNum)
        {
            var sasUri = "";
            if (argNum > args.Length)
            {
                return sasUri;
            }
            else
            {
                try
                {
                    var sasUriString = args[argNum];
                    if (ArguementIsAParameter(sasUriString))
                    {
                        var optionString = args[argNum].Substring(1, args[argNum].Length - 1);

                        if (optionString.StartsWith("blobsasuri:", StringComparison.OrdinalIgnoreCase))
                        {
                            sasUri = optionString.Substring("blobsasuri:".Length);
                            argNum++;
                        }
                    }
                }
                catch
                {
                }
            }

            return sasUri;
        }

        private static Process GetMainSiteW3wpProcess()
        {
            Console.WriteLine("Getting main site's w3wp process");

            bool inScmSite = false;
            var homePath = Environment.GetEnvironmentVariable("HOME_EXPANDED");
            string siteName = Environment.GetEnvironmentVariable("WEBSITE_IIS_SITE_NAME") != null ? Environment.GetEnvironmentVariable("WEBSITE_IIS_SITE_NAME").ToString() : "";
            if (homePath.Contains(@"\DWASFiles\Sites\#") || siteName.StartsWith("~"))
            {
                inScmSite = true;
            }

            var parentProcess = Process.GetCurrentProcess();
            Process mainSiteW3wpProcess = null;
            while (parentProcess != null)
            {
                if (!parentProcess.ProcessName.Equals("w3wp", StringComparison.OrdinalIgnoreCase))
                {
                    parentProcess = parentProcess.GetParentProcess();
                    continue;
                }

                if (inScmSite)
                {
                    mainSiteW3wpProcess = Process.GetProcessesByName("w3wp").FirstOrDefault(p => p.Id != parentProcess.Id);
                }
                else
                {
                    mainSiteW3wpProcess = parentProcess;
                }
                break;
            }

            if (mainSiteW3wpProcess == null)
            {
                Console.WriteLine("Woah, I missed it. Where did w3wp go?");
            }
            return mainSiteW3wpProcess;
        }

        private static TimeSpan GetTimeSpanFromArg(string[] args, ref int argNum)
        {
            int numberOfSeconds = 30;
            try
            {
                var numberOfSecondsStr = args[argNum];
                if (!ArguementIsAParameter(numberOfSecondsStr))
                {
                    numberOfSeconds = int.Parse(numberOfSecondsStr);
                    argNum++;
                }
            }
            catch
            {
                // No timespan is specified. We'll default to 30 seconds;
            }

            return TimeSpan.FromSeconds(numberOfSeconds);
        }

        private static bool GetRunAllInstancesFromArg(string[] args, ref int argNum)
        {
            if (argNum > args.Length)
            {
                return false;
            }
            else
            {
                try
                {
                    var allInstancesString = args[argNum];
                    if (ArguementIsAParameter(allInstancesString))
                    {
                        Options.TryParse(args[argNum].Substring(1, args[argNum].Length - 1), true, out Options option);
                        if (option == Options.AllInstances)
                        {
                            argNum++;
                            return true;
                        }

                    }
                }
                catch
                {
                    // Assume Options.AllInstances is false if not specified
                }
            }

            return false;
        }

        private static bool ArguementIsAParameter(string currentArgument)
        {
            return currentArgument[0].Equals('-') || currentArgument[0].Equals('/');
        }

        private static List<Diagnoser> GetDiagnosersToRun(string[] args, ref int argNum)
        {
            var diagnosers = SessionController.GetAllDiagnosers().ToList();

            var diagnosersToRun = new List<Diagnoser>();
            while (argNum < args.Length)
            {
                if (ArguementIsAParameter(args[argNum]))
                {
                    // Done parsing all diagnosers
                    break;
                }

                int timeSpan;
                if (int.TryParse(args[argNum], out timeSpan))
                {
                    // Done parsing all diagnosers, we've reached the timespan now
                    break;
                }

                var diagnoserName = args[argNum];
                argNum++;

                Diagnoser diagnoser = null;
                try
                {
                    diagnoser = diagnosers.First(d => d.Name.Equals(diagnoserName, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    Console.WriteLine("There is no diagnoser called {0}. Valid names are below.", diagnoserName);
                    ListDiagnosers(diagnosers);
                    Environment.Exit(-1);
                }

                diagnosersToRun.Add(diagnoser);
            }

            if (diagnosersToRun.Count == 0)
            {
                Console.WriteLine("You must specify at least one diagnoser to run. Valid diagnosers are:");
                ListDiagnosers(diagnosers);
                Environment.Exit(-1);
            }

            Console.WriteLine("Will use the following diagnosers:");
            foreach (var diagnoser in diagnosersToRun)
            {
                Console.WriteLine("  " + diagnoser.Name);
            }

            return diagnosersToRun;
        }

        private static void ListDiagnosers(IEnumerable<Diagnoser> diagnosers)
        {
            Console.WriteLine("Diagnosers:");
            foreach (var diagnoser in diagnosers)
            {
                Console.WriteLine("   " + diagnoser.Name);
                if (!string.IsNullOrEmpty(diagnoser.Description))
                {
                    Console.WriteLine("        Description: " + diagnoser.Description);
                }
                foreach (string warning in diagnoser.GetWarnings())
                {
                    Console.WriteLine("        Warning: " + warning);
                }
            }
        }

        private static void ShowUsage()
        {
            var optionDescriptions = new List<Argument>()
            {
                new Argument() {Command = Options.Troubleshoot, Usage = "<Diagnoser1> [<Diagnoser2> ...] [TimeSpanToRunForInSeconds] [-BlobSasUri:\"<A_VALID_BLOB_SAS_URI>\"] [-AllInstances]", Description = "Create a new Collect and Analyze session with the requested diagnosers. Default TimeSpanToRunForInSeconds is 30. If a valid BlobSasUri is specified, all data for the session will be stored on the specified blob account. By default this option collects data only on the current instance. To collect the data on all the instances specify -AllInstances."},
                new Argument() {Command = Options.CollectLogs, Usage = "<Diagnoser1> [<Diagnoser2> ...] [TimeSpanToRunForInSeconds] [-BlobSasUri:\"<A_VALID_BLOB_SAS_URI>\"] [-AllInstances]", Description = "Create a new Collect Only session with the requested diagnosers. Default TimeSpanToRunForInSeconds is 30. If a valid BlobSasUri is specified, all data for the session will be stored on the specified blob account. By default this option collects data only on the current instance. To collect the data on all the instances specify -AllInstances."},
                new Argument() {Command = Options.CollectKillAnalyze, Usage = "<Diagnoser1> [<Diagnoser2> ...] [TimeSpanToRunForInSeconds] [-BlobSasUri:\"<A_VALID_BLOB_SAS_URI>\"] [-AllInstances]", Description = "Create a new Collect Only session with the requested diagnosers, kill the main site's w3wp process to restart w3wp, then analyze the collected logs. Default TimeSpanToRunForInSeconds is 30. If a valid BlobSasUri is specified, all data for the session will be stored on the specified blob account. By default this option collects data only on the current instance. To collect the data on all the instances specify -AllInstances."},
                new Argument() {Command = Options.ListDiagnosers, Usage = "", Description = "List all available diagnosers"},
                new Argument() {Command = Options.ListSessions, Usage = "", Description = "List all sessions"},
                new Argument() {Command = Options.GetSasUri, Usage = "", Description = "Get the blob storage Sas Uri"},
                new Argument() {Command = Options.SetSasUri, Usage = "<SasUri>", Description = "Set the blob storage Sas Uri"},
                new Argument() {Command = Options.Setup, Usage = "", Description = "Start the continuous webjob runner (if it's already started this does nothing)"},
                new Argument() {Command = Options.GetSetting, Usage = "<SettingName>", Description = "The the value of the given setting"},
                new Argument() {Command = Options.SetSetting, Usage = "<SettingName> <SettingValue>", Description = "Save new value for the given setting"},
                new Argument() {Command = Options.ListInstances, Usage = "", Description = "List all live instances"},
                new Argument() {Command = Options.AnalyzeSession, Usage = "<SessionId>", Description = "Begin analysis for session with the specified ID"},
                new Argument() {Command = Options.CancelSession, Usage = "<SessionId>", Description = "Cancel session with the specified ID"},
            };

            Console.WriteLine("\n Usage: DaasConsole.exe -<parameter1> [param1 args] [-parameter2 ...]\n");
            Console.WriteLine(" Parameters:\n");

            foreach (var option in optionDescriptions)
            {
                Console.WriteLine("   -{0} {1}", option.Command, option.Usage);
                Console.WriteLine("       {0}\n", option.Description);
            }

            Console.WriteLine(" Examples:");
            Console.WriteLine();
            Console.WriteLine("   To list all diagnosers run:");
            Console.WriteLine("       DaasConsole.exe -ListDiagnosers");
            Console.WriteLine();
            Console.WriteLine("   To collect and analyze memory dumps run");
            Console.WriteLine("       DaasConsole.exe -Troubleshoot \"Memory Dump\" 60");
            Console.WriteLine();
            Console.WriteLine("   To collect memory dumps, kill w3wp, and then analyze the logs run");
            Console.WriteLine("       DaasConsole.exe -CollectKillAnalyze \"Memory Dump\" 60 ");
            Console.WriteLine();
            Console.WriteLine("To specify a custom folder to get the diagnostic tools from, set the DiagnosticToolsPath setting to the desired location");
        }
    }
}
