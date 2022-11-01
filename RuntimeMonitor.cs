using System.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;


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

class DiagnosticActivityInfo
{
    public string eventName { get;}
    public string id { get;}
    public DateTime? stopTime { get; set; }
    public DiagnosticActivityInfo(string name, string activityID)
    {
        eventName = name;
        id = activityID;
        stopTime = null;
    }

    public override string ToString()
    {
        return $"{eventName}:\n\t{id}\n"+ (stopTime != null ? stopTime.ToString() : "no stop time");
    }
}

class RuntimeActivityEventListener : EventListener
{
    private ILogger m_logger;
    private static object s_consoleLock = new object();
    private Dictionary<string, Stack<DiagnosticActivityInfo>> eventGroupings = new Dictionary<string, Stack<DiagnosticActivityInfo>>();

    public RuntimeActivityEventListener(ILogger logger)
    {
        m_logger = logger;
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            // Keywords 0x1 are for GC events and 0x10 are for JIT events
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)(0x1 | 0x10));
        }
        else if (eventSource.Name == "Microsoft-Diagnostics-DiagnosticSource")
        {
            // DiagnosticSourceEventSource has this domain specific language to tell it what
            // you want to get events for. '[AS]*' tells it to give you all System.Diagnostic.Activity
            // events.
            Dictionary<string, string?> args = new Dictionary<string, string?>();
            args["FilterAndPayloadSpecs"] = "[AS]*";
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x2, args);
        }
        else if (eventSource.Name == "System.Threading.Tasks.TplEventSource") 
        {
            // Activity IDs aren't enabled by default.
            // Enabling Keyword 0x80 on the TplEventSource turns them on
            EnableEvents(eventSource, EventLevel.Informational, (EventKeywords)0x80);
        }
        else if (eventSource.Name == "ActivityGeneratingEventSource") 
        {
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        String eventDataActivityID = eventData.ActivityId.ToString();
        //Console.WriteLine($"{eventData.EventName} has an eventData.ActivityID of {eventDataActivityID} and");
        //Console.WriteLine($"{eventData.EventName}:aid of {eventDataActivityID} & thread id of {eventData.OSThreadId} & timestamp of {eventData.TimeStamp.Ticks}");

        lock (s_consoleLock)
        {
            if (!eventGroupings.ContainsKey(eventDataActivityID))
            {
                eventGroupings.Add(eventDataActivityID, new Stack<DiagnosticActivityInfo>());
            }

            if (eventData.EventName == "ActivityStart")
            {
                //Console.WriteLine($"{eventData.EventName} with ID: {eventDataActivityID}");
                object[] args = (object[])eventData.Payload[2];
                string activityID = GetIDFromActivityPayload(args);
                
                eventGroupings[eventDataActivityID].Push(new DiagnosticActivityInfo(eventData.EventName, activityID));
            }
            else if (eventData.EventName == "ActivityStop")
            {
                foreach (DiagnosticActivityInfo activity in eventGroupings[eventDataActivityID])
                {
                    object[] args = (object[])eventData.Payload[2];
                    string activityID = GetIDFromActivityPayload(args);

                    if (activity.id == activityID)
                    {
                        activity.stopTime = eventData.TimeStamp;
                        break;
                    }
                }
                //Console.WriteLine($"{eventData.EventName} with ID: {eventDataActivityID}");
            }
            else if (eventData.EventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                //Console.WriteLine($"{eventData.EventName} with ID: {eventDataActivityID}");
                //printStack(eventGroupings[eventDataActivityID]);
                if (eventGroupings[eventDataActivityID].Count > 0)
                {
                    while (eventGroupings[eventDataActivityID].Peek().stopTime != null && eventData.TimeStamp > eventGroupings[eventDataActivityID].Peek().stopTime)
                    {
                        eventGroupings[eventDataActivityID].Pop();
                    }

                    m_logger.LogInformation($"Runtime event {eventData.EventName} is associated with activity {eventGroupings[eventDataActivityID].Peek().id}");
                }
                else
                {
                    m_logger.LogInformation($"Could not find activity to associate to runtime event {eventData.EventName}.");
                }
            }
        }
    }

    private void printStack(Stack<DiagnosticActivityInfo> activities)
    {
        foreach (DiagnosticActivityInfo activity in activities)
        {
            Console.WriteLine(activity.ToString());
        }
    }

    private static string? GetIDFromActivityPayload(object[] arguments)
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