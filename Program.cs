using System.Reflection.Emit;
using System.Reflection;
using System.Diagnostics.Tracing;
using System.Diagnostics;
using System;
using System.Threading.Tasks;
using System.Collections;
using Microsoft.Extensions.Logging;

class Program
{
    delegate long SquareItInvoker(int input);

    delegate TReturn OneParameter<TReturn, TParameter0>
        (TParameter0 p0);


    static async Task GenerateJITs(RuntimeContext context) 
    {
        using (var monitor = RuntimeEventMonitor.StartNew(context)) 
        {
            // Using without parentheses or brackets makes it live for the method body
            using RuntimeActivityEventListener listener = new RuntimeActivityEventListener();

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

    static async Task RunRuntime(ILogger logger)
    {
        RuntimeContext context = new RuntimeContext(logger);
        logger.LogInformation("Starting Runtime");
        await GenerateJITs(context);
        logger.LogInformation("Completed Runtime");

    }

    static async Task Main(string[] args)
    {
        ILoggerFactory factory = LoggerFactory.Create(options =>
        {
            options.AddSimpleConsole(options => options.SingleLine = true);
        });

        await RunRuntime(factory.CreateLogger("Service"));
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

public class RuntimeContext 
{
    ILogger _logger;

    public RuntimeContext(ILogger logger) => _logger = logger;

    public void LogRuntimeEvent(string runtimeEvent, string activityID) 
    {
        _logger.LogInformation(string.IsNullOrEmpty(activityID) ? $"Runtime event {runtimeEvent} has no associated activity" : $"Runtime event {runtimeEvent} is associated with activity {activityID}" );
    }
}

