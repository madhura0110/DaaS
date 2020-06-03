﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using DaaS;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace ClrProfilingAnalyzer.Parser
{
    public class AspNetCoreRequestParser
    {
        const string MicrosoftExtensionsLoggingProvider = "Microsoft-Extensions-Logging";
        const string StartEvent = "ActivityStart/Start";
        const string StopEvent = "ActivityStop/Stop";
        const string FormatMessageEvent = "FormattedMessage";
        static readonly string[] AspNetCoreHostingLoggers = { "Microsoft.AspNetCore.Hosting.Internal.WebHost", "Microsoft.AspNetCore.Hosting.Diagnostics" };

        const int MAX_TRACEMESSAGES_IN_FULL_TRACE = 1000;
        const int MAX_REQUEST_COUNT_TO_TRACE = 1000;
        const int MAX_FAILED_REQUESTS_TO_TRACE = 500;

        public static AspNetCoreParserResults ParseDotNetCoreRequests(Microsoft.Diagnostics.Tracing.Etlx.TraceLog dataFile, int minRequestDurationMilliseconds)
        {
            AspNetCoreParserResults results = new AspNetCoreParserResults();

            var processes = new List<AspNetCoreProcess>();
            var aspnetCoreRequests = new Dictionary<string, AspNetCoreRequest>();
            var requestsFullTrace = new Dictionary<AspNetCoreRequestId, List<AspNetCoreTraceEvent>>();
            var failedRequests = new Dictionary<AspNetCoreRequest, List<AspNetCoreTraceEvent>>();

            try
            {
                var source = dataFile.Events.GetSource();
                var parser = new DynamicTraceEventParser(source);
                var clrParser = new ClrTraceEventParser(source);

                parser.AddCallbackForProviderEvent(MicrosoftExtensionsLoggingProvider, StartEvent, delegate (TraceEvent data)
                {
                    ParseExtensionsLoggingEvent(data,
                                                minRequestDurationMilliseconds,
                                                "Arguments",
                                                aspnetCoreRequests,
                                                failedRequests,
                                                requestsFullTrace,
                                                AspNetCoreRequestEventType.Start);
                });

                parser.AddCallbackForProviderEvent(MicrosoftExtensionsLoggingProvider, StopEvent, delegate (TraceEvent data)
                {
                    ParseExtensionsLoggingEvent(data,
                                                minRequestDurationMilliseconds,
                                                string.Empty,
                                                aspnetCoreRequests,
                                                failedRequests,
                                                requestsFullTrace,
                                                AspNetCoreRequestEventType.Stop);
                });

                parser.AddCallbackForProviderEvent(MicrosoftExtensionsLoggingProvider, FormatMessageEvent, delegate (TraceEvent data)
                {
                    ParseExtensionsLoggingEvent(data,
                                                minRequestDurationMilliseconds,
                                                "FormattedMessage",
                                                aspnetCoreRequests,
                                                failedRequests,
                                                requestsFullTrace,
                                                AspNetCoreRequestEventType.Message);
                });

                clrParser.ThreadPoolWorkerThreadWait += delegate (ThreadPoolWorkerThreadTraceData data)
                {
                    if (!processes.Any(p => p.Id == data.ProcessID && p.Name == data.ProcessName))
                    {
                        var coreProcess = new AspNetCoreProcess
                        {
                            Id = data.ProcessID,
                            Name = data.ProcessName
                        };
                        processes.Add(coreProcess);
                    }
                };

                source.Process();
            }
            catch (Exception ex)
            {
                Logger.LogDiagnoserErrorEvent("Failed while parsing .net core events", ex);
            }

            foreach (var request in aspnetCoreRequests.Values)
            {
                if (request.EndTimeRelativeMSec == 0)
                {
                    request.EndTimeRelativeMSec = dataFile.SessionEndTimeRelativeMSec;
                    request.IncompleteRequest = true;
                }
            }

            results.AspNetCoreRequestsFullTrace = requestsFullTrace;
            results.Requests = aspnetCoreRequests;
            results.Processes = processes;
            results.FailedRequests = failedRequests;
            return results;
        }

        private static void ParseExtensionsLoggingEvent(TraceEvent data,
                                                        int minRequestDurationMilliseconds,
                                                        string eventArgs,
                                                        Dictionary<string, AspNetCoreRequest> aspnetCoreRequests,
                                                        Dictionary<AspNetCoreRequest, List<AspNetCoreTraceEvent>> failedRequests,
                                                        Dictionary<AspNetCoreRequestId, List<AspNetCoreTraceEvent>> requestsFullTrace,
                                                        AspNetCoreRequestEventType eventType)
        {
            var loggerName = data.PayloadByName("LoggerName").ToString();
            string rawMessage = "";

            if (!string.IsNullOrWhiteSpace(eventArgs))
            {
                if (data.PayloadByName(eventArgs) != null)
                {
                    rawMessage = data.PayloadByName(eventArgs).ToString();
                    if (rawMessage.ToLower().Contains("StructValue[]".ToLower()))
                    {
                        rawMessage = "";
                        try
                        {
                            var args = (IDictionary<string, object>[])data.PayloadByName(eventArgs);
                            foreach (IDictionary<string, object> item in args.ToList())
                            {
                                var dict = item.ToDictionary(x => x.Key, x => x.Value);
                                rawMessage += $" {dict["Key"].ToString()}->[{dict["Value"].ToString()}]";
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                    if (rawMessage.Length > 250)
                    {
                        rawMessage = rawMessage.Substring(0, 250) + "...[REMOVED_AS_MESSAGE_TOO_LARGE]";
                    }
                    rawMessage = eventType.ToString() + ":" + rawMessage;
                }
            }
            else
            {
                rawMessage = eventType.ToString();
            }

            var shortActivityId = StartStopActivityComputer.ActivityPathString(data.ActivityID);
            foreach (var key in requestsFullTrace.Keys.ToArray())
            {
                if (shortActivityId.StartsWith(key.ShortActivityId))
                {
                    AddRawAspNetTraceToDictionary(key, shortActivityId, loggerName, rawMessage, data, requestsFullTrace);
                    break;
                }
            }

            if (CheckAspNetLogger(loggerName) && eventType == AspNetCoreRequestEventType.Start)
            {
                if (data.ActivityID != Guid.Empty)
                {
                    if (!aspnetCoreRequests.ContainsKey(shortActivityId))
                    {
                        var coreRequest = new AspNetCoreRequest
                        {
                            ShortActivityId = shortActivityId,
                            ProcessId = data.ProcessID,
                            ActivityId = data.ActivityID,
                            RelatedActivityId = StartStopActivityComputer.ActivityPathString(data.RelatedActivityID)
                        };
                        var arguments = (IDictionary<string, object>[])data.PayloadByName("Arguments");

                        GetAspnetCoreRequestDetailsFromArgs(arguments.ToList(), out coreRequest.Path, out coreRequest.RequestId);
                        coreRequest.StartTimeRelativeMSec = data.TimeStampRelativeMSec;

                        if (!string.IsNullOrWhiteSpace(coreRequest.Path) && !string.IsNullOrWhiteSpace(coreRequest.RequestId))
                        {
                            aspnetCoreRequests.Add(shortActivityId, coreRequest);
                        }
                    }

                    AspNetCoreRequestId requestId = new AspNetCoreRequestId
                    {
                        ShortActivityId = shortActivityId,
                        ActivityId = data.ActivityID
                    };
                    AddRawAspNetTraceToDictionary(requestId, shortActivityId, loggerName, rawMessage, data, requestsFullTrace);

                }
            }
            if (CheckAspNetLogger(loggerName) && eventType == AspNetCoreRequestEventType.Stop)
            {
                if (data.ActivityID != Guid.Empty)
                {
                    if (aspnetCoreRequests.TryGetValue(shortActivityId, out AspNetCoreRequest coreRequest))
                    {
                        //
                        // We are setting EndTime in 'Request finished' as well. Not
                        // sure which is the correct one right now, so doing it both.
                        //
                        coreRequest.EndTimeRelativeMSec = data.TimeStampRelativeMSec;
                        if ((coreRequest.EndTimeRelativeMSec - coreRequest.StartTimeRelativeMSec) < minRequestDurationMilliseconds)
                        {
                            var keyToRemove = requestsFullTrace.Keys.Where(x => x.ShortActivityId == coreRequest.ShortActivityId).FirstOrDefault();
                            if (keyToRemove != null)
                            {
                                requestsFullTrace.Remove(keyToRemove);
                            }
                        }
                    }

                }
            }
            if (CheckAspNetLogger(loggerName) && eventType == AspNetCoreRequestEventType.Message)
            {
                string formattedMessage = string.Empty;
                if (data.PayloadByName("FormattedMessage") != null)
                {
                    formattedMessage = data.PayloadByName("FormattedMessage").ToString();
                }
                else if (data.PayloadByName("EventName") != null)
                {
                    formattedMessage = data.PayloadByName("EventName").ToString();
                }

                if (data.ActivityID != Guid.Empty)
                {
                    if (formattedMessage.StartsWith("Request finished", StringComparison.OrdinalIgnoreCase))
                    {
                        if (aspnetCoreRequests.TryGetValue(shortActivityId, out AspNetCoreRequest coreRequest))
                        {
                            int statusCode = GetStatusCodeFromRequestFinishedMessage(formattedMessage);
                            if (statusCode > 0)
                            {
                                coreRequest.StatusCode = statusCode;
                                coreRequest.EndTimeRelativeMSec = data.TimeStampRelativeMSec;
                            }
                            if (statusCode >= 500)
                            {
                                AspNetCoreRequestId requestId = new AspNetCoreRequestId
                                {
                                    ShortActivityId = shortActivityId,
                                    ActivityId = data.ActivityID
                                };

                                var requestFullTraceFailedRequest = requestsFullTrace.Where(x => x.Key.ShortActivityId == coreRequest.ShortActivityId).FirstOrDefault();
                                if (requestFullTraceFailedRequest.Value != null && failedRequests.Count() < MAX_FAILED_REQUESTS_TO_TRACE)
                                {
                                    failedRequests.Add(coreRequest.Clone(), requestFullTraceFailedRequest.Value.ToArray().ToList());
                                }
                            }
                        }
                    }

                }
            }
        }

        private static bool CheckAspNetLogger(string loggerName)
        {
            return AspNetCoreHostingLoggers.Contains(loggerName);
        }

        private static void AddRawAspNetTraceToDictionary(AspNetCoreRequestId id, string relatedActivityId, string loggerName, string rawMessage, TraceEvent data, Dictionary<AspNetCoreRequestId, List<AspNetCoreTraceEvent>> requestsFullTrace)
        {
            AspNetCoreTraceEvent eventAspNet = new AspNetCoreTraceEvent
            {
                TimeStampRelativeMSec = data.TimeStampRelativeMSec,
                Message = $"{loggerName} {rawMessage}",
                RelatedActivity = relatedActivityId
            };

            if (requestsFullTrace.TryGetValue(id, out List<AspNetCoreTraceEvent> traceEvents))
            {
                if (traceEvents.Count < MAX_TRACEMESSAGES_IN_FULL_TRACE)
                {
                    traceEvents.Add(eventAspNet);
                }
            }
            else
            {
                if (requestsFullTrace.Count < MAX_REQUEST_COUNT_TO_TRACE)
                {
                    List<AspNetCoreTraceEvent> traceEventsList = new List<AspNetCoreTraceEvent>
                    {
                        eventAspNet
                    };

                    requestsFullTrace.Add(id, traceEventsList);
                }
            }
        }

        //Request finished in 6237.5768ms 200 application/json; charset=utf-8" ActivityID="/#29224/1/30/1/
        private static int GetStatusCodeFromRequestFinishedMessage(string formattedMessage)
        {
            int statusCode = -1;
            int removeAt = formattedMessage.IndexOf("ms ");
            if (removeAt > 0)
            {
                formattedMessage = formattedMessage.Substring(removeAt + 3);
                var array = formattedMessage.Split(' ');
                if (array.Length > 0)
                {
                    int.TryParse(array[0], out statusCode);
                }
            }
            return statusCode;
        }
        private static void GetAspnetCoreRequestDetailsFromArgs(List<IDictionary<string, object>> arguments, out string requestPath, out string requestId)
        {
            requestPath = string.Empty;
            requestId = string.Empty;

            foreach (IDictionary<string, object> item in arguments)
            {
                var dict = item.ToDictionary(x => x.Key, x => x.Value);

                if ((string)dict["Key"] == "RequestId")
                {
                    requestId = dict["Value"].ToString();
                }
                if ((string)dict["Key"] == "RequestPath")
                {
                    requestPath = dict["Value"].ToString();
                }
            }
        }
    }
}
