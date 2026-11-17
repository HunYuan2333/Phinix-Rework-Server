using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Utils.Framework;
namespace PhinixServer.Framework
{
    // Configuration and payload exception messages may contain private values.
    // Keep original types/causes/frames without mutating or replacing the exception.
    internal static class ServerDiagnosticText
    {
        internal static string Format(HostLogEntry entry)
        {
            if(entry==null)return string.Empty;
            if(entry.Exception==null)return entry.ToDisplayMessage();
            try
            {
                var text=new StringBuilder("["+entry.ExtensionId+"] "+entry.Message+(string.IsNullOrEmpty(entry.CorrelationId)?string.Empty:" [correlation="+entry.CorrelationId+"]")+"\n[exception messages omitted]\n");
                var queue=new Queue<Exception>();queue.Enqueue(entry.Exception);int remaining=64;
                while(queue.Count>0 && remaining-->0 && text.Length<65500)
                {
                    var error=queue.Dequeue();text.AppendLine(error.GetType().FullName);text.AppendLine(new StackTrace(error,false).ToString());
                    if(error is AggregateException aggregate){foreach(var cause in aggregate.InnerExceptions)if(queue.Count<64)queue.Enqueue(cause);}
                    else if(error.InnerException!=null)queue.Enqueue(error.InnerException);
                    if(error is System.Reflection.ReflectionTypeLoadException loader && loader.LoaderExceptions!=null)
                        foreach(var cause in loader.LoaderExceptions)if(cause!=null && queue.Count<64)queue.Enqueue(cause);
                }
                return text.ToString(0,Math.Min(text.Length,65500))+(queue.Count>0 || text.Length>65500?"\n[diagnostic size limit reached]":"");
            }
            catch {return "[exception diagnostic formatting unavailable; messages omitted]";}
        }
    }
}
