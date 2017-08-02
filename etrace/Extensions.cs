using System;
using System.Diagnostics;
using System.Text;
using Microsoft.Diagnostics.Tracing;

namespace etrace
{
    internal static class Extensions
    {
        public static string AsRawString(this TraceEvent e)
        {
            var sb = new StringBuilder();
            var processById = Process.GetProcessById(e.ProcessID);
            
            sb.Append($"{e.EventName} [PNAME={e.ProcessName} PID={e.ProcessID} TID={e.ThreadID} TIME={e.TimeStamp}] TaskName={e.TaskName} ProcessName:{processById.ProcessName} EventName:{e.EventName}");
            for (var i = 0; i < e.PayloadNames.Length; ++i)
                try
                {
                    sb.AppendFormat("\n  {0,-20} = {1}", e.PayloadNames[i], e.PayloadValue(i));
                }
                catch (ArgumentOutOfRangeException)
                {
                    // TraceEvent sometimes throws this exception from PayloadValue(i).
                }
            return sb.ToString();
        }


        public static int GetExpectedFieldWidth(string field)
        {
            switch (field)
            {
                case "Event":
                    return 20;
                case "PID":
                    return 5;
                case "TID":
                    return 5;
                case "Time":
                    return 15;
                default:
                    return 30;
            }
        }

        public static string GetFieldByName(this TraceEvent e, string field)
        {
            if (field == "Event")
                return e.EventName;
            if (field == "PID")
                return e.ProcessID.ToString();
            if (field == "TID")
                return e.ThreadID.ToString();
            if (field == "Time")
                return e.TimeStamp.ToString();

            var value = e.PayloadByName(field);
            if (value != null)
                return value.ToString();
            return "<null>";
        }

        public static string Truncate(this string s, int length)
        {
            if (s.Length <= length)
                return s;

            return s.Substring(0, length - 3) + "...";
        }
    }
}