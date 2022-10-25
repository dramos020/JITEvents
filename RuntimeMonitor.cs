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

class RuntimeActivityEventListener : EventListener
{
    private ILogger m_logger;
    private static object s_consoleLock = new object();
    public RuntimeActivityEventListener(ILogger logger)
    {
        m_logger = logger;
    }

    public Dictionary<string, Stack<EventWrittenEventArgs>> eventGroupings = new Dictionary<string, Stack<EventWrittenEventArgs>>();

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        base.OnEventSourceCreated(eventSource);

        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            // Keyword 0x11 is Runtime events
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)(0x1 | 0x10));
        }
        else if (eventSource.Name == "Microsoft-Diagnostics-DiagnosticSource")
        {
            Dictionary<string, string> args = new Dictionary<string, string>();
            // DiagnosticSourceEventSource has this domain specific language to tell it what
            // you want to get events for. '[AS]*' tells it to give you all System.Diagnostic.Activity
            // events.
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
        base.OnEventWritten(eventData);

        String eventDataActivityID = eventData.ActivityId.ToString();
        lock (s_consoleLock)
        {
            if (!eventGroupings.ContainsKey(eventDataActivityID))
            {
                eventGroupings.Add(eventDataActivityID, new Stack<EventWrittenEventArgs>());
            }

            if (eventData.EventName == "ActivityStart")
            {
                Console.WriteLine($"{eventData.EventName} with ID: {eventDataActivityID}");
                eventGroupings[eventDataActivityID].Push(eventData);
            }
            else if (eventData.EventName == "ActivityStop")
            {
                Console.WriteLine($"{eventData.EventName} with ID: {eventDataActivityID}");

                eventGroupings[eventDataActivityID].Pop();
            }
            else
            {
                Console.WriteLine($"{eventData.EventName} with ID: {eventDataActivityID}");
                if (eventGroupings[eventDataActivityID].Count > 0)
                {
                    EventWrittenEventArgs mappedActivity = eventGroupings[eventDataActivityID].Peek();
                    object[] arguments = (object[])mappedActivity.Payload[2];
                    string activityID = GetIDFromActivityPayload(arguments);

                    //m_logger.LogInformation($"Runtime event {eventData.EventName} is associated with activity {activityID}" );
                }
                else
                {
                    //m_logger.LogInformation($"Runtime event {eventData.EventName} has no associated activity.");
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