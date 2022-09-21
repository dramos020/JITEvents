using System.Reflection.Emit;
using System.Reflection;
using System.Diagnostics.Tracing;
using System.Diagnostics;
using System;
using System.Threading.Tasks;
using System.Collections;
using Microsoft.Extensions.Logging;


public struct MyEventMonitor : IDisposable 
{
    public static MyEventMonitor StartNew(JITContext context) 
    { 
        MyEventListener.Singleton.t_currentContext.Value = context;
        return new MyEventMonitor();
    }

    public void Dispose() 
    {
        MyEventListener.Singleton.t_currentContext.Value = null;
    }
}

class MyEventListener : EventListener
{
    private static object s_consoleLock = new object();
    static internal MyEventListener Singleton = new MyEventListener();
    internal AsyncLocal<JITContext?> t_currentContext = new AsyncLocal<JITContext?>();

    public Dictionary<string, Stack<EventWrittenEventArgs>> eventGroupings = new Dictionary<string, Stack<EventWrittenEventArgs>>();

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        base.OnEventSourceCreated(eventSource);

        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            // Keyword 0x10 is JIT events
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x10);
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
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        base.OnEventWritten(eventData);

        String eventDataActivityID = eventData.ActivityId.ToString();

        if ((eventData.EventName.StartsWith("MethodJittingStarted")
        || eventData.EventName.StartsWith("Activity")) 
        && !eventGroupings.ContainsKey(eventDataActivityID))
        {
            eventGroupings.Add(eventDataActivityID, new Stack<EventWrittenEventArgs>());
        }

        lock (s_consoleLock)
        {
            if (eventData.EventName.StartsWith("MethodJittingStarted"))
            {
                if (eventGroupings[eventDataActivityID].Count > 0)
                {
                    EventWrittenEventArgs mappedActivity = eventGroupings[eventDataActivityID].Peek();
                    object[] arguments = (object[])mappedActivity.Payload[2];
                    string activityID = GetIDFromActivityPayload(arguments);

                    t_currentContext.Value?.AddCheckpoint(eventData.EventName, activityID);
                }
                else
                {
                    t_currentContext.Value?.AddCheckpoint(eventData.EventName, null);
                }
            }

            if (eventData.EventName == "ActivityStart")
            {
                eventGroupings[eventDataActivityID].Push(eventData);
            }

            if (eventData.EventName == "ActivityStop")
            {
                eventGroupings[eventDataActivityID].Pop();
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



class Program
{
    delegate long SquareItInvoker(int input);

    delegate TReturn OneParameter<TReturn, TParameter0>
        (TParameter0 p0);


    static async Task GenerateJITs(JITContext context) 
    {
        using (var monitor = MyEventMonitor.StartNew(context)) 
        {
            // Using without parentheses or brackets makes it live for the method body
            using MyEventListener listener = new MyEventListener();

            // This code makes 3 Tasks, and then each task creates an Activity and then
            // causes 5 methods to be jitted.
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < 3; ++i)
            {
                Task t = Task.Run(() =>
                {
                    using (Activity activity = new Activity($"Activity {i}"))
                    {
                        activity.Start();

                        for (int i = 0; i < 5; i++)
                        {
                            MakeDynamicMethod();
                        }
                    }
                });

                tasks.Add(t);
            }

            foreach (Task t in tasks)
            {
                await t;
            }
        }
    }

    static async Task RunJITing(ILogger logger)
    {
        JITContext context = new JITContext(logger);
        logger.LogInformation("Starting JITing");
        await GenerateJITs(context);
        logger.LogInformation("Completed JITing");

    }

    static async Task Main(string[] args)
    {
        ILoggerFactory factory = LoggerFactory.Create(options =>
        {
            options.AddSimpleConsole(options => options.SingleLine = true);
        });

        await RunJITing(factory.CreateLogger("Service"));
    }

    // Don't worry about the specifics of this method too much, it uses a feature called
    // Lightweight Code Generatation (LCG) to generate new jitted methods on the fly.
    // The only reason I'm using it here is to generate a predictable number of JIT events
    // on each Activity.
    static void MakeDynamicMethod()
    {
        AssemblyName name = new AssemblyName(GetRandomName());
        AssemblyBuilder dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.RunAndCollect);
        ModuleBuilder dynamicModule = dynamicAssembly.DefineDynamicModule(GetRandomName());

        Type[] methodArgs = { typeof(int) };

        DynamicMethod squareIt = new DynamicMethod(
            "SquareIt",
            typeof(long),
            methodArgs,
            dynamicModule);

        ILGenerator il = squareIt.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ret);

        OneParameter<long, int> invokeSquareIt =
            (OneParameter<long, int>)
            squareIt.CreateDelegate(typeof(OneParameter<long, int>));

        Random random = new Random();
        invokeSquareIt(random.Next());
    }

    static string GetRandomName()
    {
        return Guid.NewGuid().ToString();
    }
}

public class JITContext 
{
    ILogger _logger;

    public JITContext(ILogger logger) => _logger = logger;

    public void AddCheckpoint(string jitEvent, string activityID) 
    {
        _logger.LogInformation(string.IsNullOrEmpty(activityID) ? $"JIT event {jitEvent} has no associated activity" : $"JIT event {jitEvent} is associated with activity {activityID}" );
    }
}

