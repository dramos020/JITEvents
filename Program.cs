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
    delegate TReturn OneParameter<TReturn, TParameter0>
        (TParameter0 p0);

    static async Task GenerateJITs(ILogger logger) 
    {
        using (RuntimeActivityEventListener listener = new RuntimeActivityEventListener(logger))
        {
            using (ActivityGeneratingEventSource eventSource = new ActivityGeneratingEventSource()){

                // This code makes 3 Tasks, and then each task creates an Activity and then
                // causes 5 methods to be jitted.
                eventSource.UserDefinedStart();
                for (int i = 0; i < 3; ++i)
                {
                    await Task.Run(() =>
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
                }
                eventSource.UserDefinedStop();
            }
            // used so that the process does not end before events are sent
            Thread.Sleep(1000);
        }
    }

    static async Task RunRuntime(ILogger logger)
    {
        logger.LogInformation("Starting Runtime");
        await GenerateJITs(logger);
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

    // Uses Lightweight Code Generatation (LCG) to generate new jitted methods on the fly.
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