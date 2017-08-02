using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommandLine;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace etrace
{
    internal class Program
    {
        private static readonly Options Options = new Options();
        private static IMatchedEventProcessor _eventProcessor;
        private static TraceEventSession _session;
        private static ulong _processedEvents;
        private static ulong _notFilteredEvents;
        private static Stopwatch _sessionStartStopwatch;
        private static bool _statsPrinted;

        private static void Main(string[] args)
        {
            Parser.Default.ParseArguments(args, Options);
            Options.PostParse();

            if (Options.List != ListFlags.None)
            {
                List();
                return;
            }

            // TODO Can try TraceLog support for realtime stacks as well
            // TODO One session for both kernel and CLR is not supported on Windows 7 and older

            _sessionStartStopwatch = Stopwatch.StartNew();
            Console.WriteLine($"Processing start time: {DateTime.Now}");
            CreateEventProcessor();
            using (_eventProcessor)
            {
                if (Options.IsFileSession)
                    FileSession();
                else
                    RealTimeSession();
            }

            CloseSession();
        }

        private static void CreateEventProcessor()
        {
            if (Options.StatsOnly)
                _eventProcessor = new EventStatisticsAggregator();
            else if (Options.DisplayFields.Count > 0)
                _eventProcessor = new EveryEventTablePrinter(Options.DisplayFields);
            else
                _eventProcessor = new EveryEventPrinter();
        }

        private static void FileSession()
        {
            if (Options.ClrKeywords.Count > 0 || Options.KernelKeywords.Count > 0 || Options.OtherProviders.Count > 0)
                Bail("Specifying keywords and/or providers is not supported when parsing ETL files");

            using (var source = new ETWTraceEventSource(Options.File))
            {
                ProcessTrace(source);
            }
        }

        private static void CloseSession()
        {
            lock (typeof(Program))
            {
                var eventsLost = 0;
                if (_eventProcessor != null)
                    _eventProcessor.Dispose();
                if (_session != null)
                {
                    eventsLost = _session.EventsLost;
                    _session.Dispose();
                    _session = null;
                }

                if (!_statsPrinted)
                {
                    Console.WriteLine();
                    Console.WriteLine("{0,-30} {1}", "Processing end time:", DateTime.Now);
                    Console.WriteLine("{0,-30} {1}", "Processing duration:", _sessionStartStopwatch.Elapsed);
                    Console.WriteLine("{0,-30} {1}", "Processed events:", _processedEvents);
                    Console.WriteLine("{0,-30} {1}", "Displayed events:", _notFilteredEvents);
                    Console.WriteLine("{0,-30} {1}", "Events lost:", eventsLost);
                    _statsPrinted = true;
                }
            }
        }

        private static void RealTimeSession()
        {
            if (Options.ParsedClrKeywords == 0 &&
                Options.ParsedKernelKeywords == KernelTraceEventParser.Keywords.None &&
                Options.OtherProviders.Count == 0)
                Bail("No events to collect");

            Console.CancelKeyPress += (_, __) => CloseSession();

            if (Options.DurationInSeconds > 0)
                Task.Delay(TimeSpan.FromSeconds(Options.DurationInSeconds))
                    .ContinueWith(_ => CloseSession());

            using (_session = new TraceEventSession("etrace-realtime-session"))
            {
                if (Options.ParsedKernelKeywords != KernelTraceEventParser.Keywords.None)
                    _session.EnableKernelProvider(Options.ParsedKernelKeywords);
                if (Options.ParsedClrKeywords != 0)
                    _session.EnableProvider(ClrTraceEventParser.ProviderGuid,
                        matchAnyKeywords: (ulong) Options.ParsedClrKeywords);
                if (Options.OtherProviders.Any())
                    foreach (var provider in Options.OtherProviders)
                    {
                        Guid guid;
                        if (Guid.TryParse(provider, out guid))
                        {
                            _session.EnableProvider(Guid.Parse(provider));
                        }
                        else
                        {
                            guid = TraceEventProviders.GetProviderGuidByName(provider);
                            if (guid != Guid.Empty)
                                _session.EnableProvider(guid);
                        }
                    }

                ProcessTrace(_session.Source);
            }
        }

        private static void ProcessTrace(TraceEventDispatcher dispatcher)
        {
            dispatcher.Clr.All += ProcessEvent;
            dispatcher.Kernel.All += ProcessEvent;
            dispatcher.Dynamic.All += ProcessEvent;

            dispatcher.Process();
        }

        private static void List()
        {
            if ((Options.List & ListFlags.CLR) != 0)
            {
                Console.WriteLine("\nSupported CLR keywords (use with --clr):\n");
                foreach (var keyword in Enum.GetNames(typeof(ClrTraceEventParser.Keywords)))
                    Console.WriteLine($"\t{keyword}");
            }
            if ((Options.List & ListFlags.Kernel) != 0)
            {
                Console.WriteLine("\nSupported kernel keywords (use with --kernel):\n");
                foreach (var keyword in Enum.GetNames(typeof(KernelTraceEventParser.Keywords)))
                    Console.WriteLine($"\t{keyword}");
            }
            if ((Options.List & ListFlags.Registered) != 0)
            {
                Console.WriteLine("\nRegistered or enabled providers (use with --other):\n");
                foreach (var provider in
                    TraceEventProviders.GetRegisteredOrEnabledProviders()
                        .Select(guid => TraceEventProviders.GetProviderName(guid))
                        .OrderBy(n => n))
                    Console.WriteLine($"\t{provider}");
            }
            if ((Options.List & ListFlags.Published) != 0)
            {
                Console.WriteLine("\nPublished providers (use with --other):\n");
                foreach (var provider in
                    TraceEventProviders.GetPublishedProviders()
                        .Select(guid => TraceEventProviders.GetProviderName(guid))
                        .OrderBy(n => n))
                    Console.WriteLine($"\t{provider}");
            }
        }

        private static void Bail(string message)
        {
            Console.WriteLine("ERROR: " + message);
            Environment.Exit(1);
        }

        private static void ProcessEvent(TraceEvent e)
        {
            ++_processedEvents;

            if (Options.ProcessID != -1 && Options.ProcessID != e.ProcessID)
                return;
            if (Options.ThreadID != -1 && Options.ThreadID != e.ThreadID)
                return;
            if (Options.Events.Count > 0 && !Options.Events.Contains(e.EventName))
                return;

            if (Options.ParsedRawFilter != null)
            {
                var s = e.AsRawString();
                if (Options.ParsedRawFilter.IsMatch(s))
                    TakeEvent(e, s);
            }
            else if (Options.ParsedFilters.Count > 0)
            {
                foreach (var filter in Options.ParsedFilters)
                    if (filter.IsMatch(e))
                    {
                        TakeEvent(e);
                        break;
                    }
            }
            else
            {
                TakeEvent(e);
            }
        }

        private static void TakeEvent(TraceEvent e, string description = null)
        {
            if (description != null)
                _eventProcessor.TakeEvent(e, description);
            else
                _eventProcessor.TakeEvent(e);

            ++_notFilteredEvents;
        }
    }
}