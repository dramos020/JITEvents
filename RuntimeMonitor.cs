using System.Diagnostics.Tracing;

public struct RuntimeEventMonitor : IDisposable
{
    public static RuntimeEventMonitor StartNew(RuntimeContext context)
    {
        RuntimeActivityEventListener.Singleton.t_currentContext.Value = context;
        return new RuntimeEventMonitor();
    }

    public void Dispose()
    {
        RuntimeActivityEventListener.Singleton.t_currentContext.Value = null;
    }
}

class ActivityGeneratingEventSource : EventSource 
{
    [Event(1)]
    public void UserDefinedStart() 
    {
        WriteEvent(1);
    }

    [Event(2)]
    public void UserDefinedStop() 
    {
        WriteEvent(2);
    }
    
}


class RuntimeActivityEventListener : EventListener
{
    private static object s_consoleLock = new object();
    static internal RuntimeActivityEventListener Singleton = new RuntimeActivityEventListener();
    internal AsyncLocal<RuntimeContext?> t_currentContext = new AsyncLocal<RuntimeContext?>();

    public Dictionary<string, Stack<EventWrittenEventArgs>> eventGroupings = new Dictionary<string, Stack<EventWrittenEventArgs>>();

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        base.OnEventSourceCreated(eventSource);

        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            // Keyword 0x11 is Runtime events
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)(0x1 | 0x10));
        }
        // else if (eventSource.Name == "Microsoft-Diagnostics-DiagnosticSource")
        // {
        //     Dictionary<string, string> args = new Dictionary<string, string>();
        //     // DiagnosticSourceEventSource has this domain specific language to tell it what
        //     // you want to get events for. '[AS]*' tells it to give you all System.Diagnostic.Activity
        //     // events.
        //     args["FilterAndPayloadSpecs"] = "[AS]*";
        //     EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x2, args);
        // }
        else if (eventSource.Name == "System.Threading.Tasks.TplEventSource") 
        {
            EnableEvents(eventSource, EventLevel.Informational, (EventKeywords)0x80);
        }
        else if (eventSource.Name == "ActivityGeneratingEventSource") 
        {
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        base.OnEventWritten(eventData);

        String eventDataActivityID = eventData.ActivityId.ToString();
        lock (s_consoleLock)
        {

            t_currentContext.Value?.LogRuntimeEvent(eventData.EventName, eventDataActivityID);
            if (!eventGroupings.ContainsKey(eventDataActivityID))
            {
                eventGroupings.Add(eventDataActivityID, new Stack<EventWrittenEventArgs>());
            }

            if (eventData.EventName == "UserDefinedStart")
            {
                eventGroupings[eventDataActivityID].Push(eventData);
            }
            else if (eventData.EventName == "UserDefinedStop")
            {
                eventGroupings[eventDataActivityID].Pop();
            }
            else
            {
                if (eventGroupings[eventDataActivityID].Count > 0)
                {
                    EventWrittenEventArgs mappedActivity = eventGroupings[eventDataActivityID].Peek();
                    object[] arguments = (object[])mappedActivity.Payload[2];
                    string activityID = GetIDFromActivityPayload(arguments);

                    //t_currentContext.Value?.LogRuntimeEvent(eventData.EventName, activityID);
                }
                else
                {
                    //t_currentContext.Value?.LogRuntimeEvent(eventData.EventName, null);
                }
            }
        }
    }

    private static string GetIDFromActivityPayload(object[] arguments)
    {
        foreach (object obj in arguments)
        {
            IDictionary<string, object> arg = (IDictionary<string, object>)obj;
            if (arg["Key"].Equals("Id"))
            {
                return (string)arg["Value"];
            }
        }
        return null;
    }
}