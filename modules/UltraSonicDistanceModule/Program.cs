namespace UltraSonicDistanceModule
{
    using System;
    using System.Device.Gpio;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Runtime.Loader;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;

    class Program
    {
        static int counter;

        private static CancellationTokenSource cts;

        // TODO: Make this configurable via Module Twin. Defaults to once every 10 seconds.
        private static TimeSpan telemetryInterval = new TimeSpan(0, 0, 10);

        private static ModuleClient ioTHubModuleClient;

        private static int GPIO_trigger = 18;
        private static int GPIO_echo = 24;

        static GpioController controller;

        static void Main(string[] args)
        {
            cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();

            Init().Wait();

            // Wait until the app unloads or is cancelled
            WhenCancelled(cts.Token).Wait();
        }

        /// <summary>
        /// Handles cleanup operations when app is cancelled or unloads
        /// </summary>
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes the ModuleClient and sets up the callback to receive
        /// messages containing temperature information
        /// </summary>
        static async Task Init()
        {
            MqttTransportSettings mqttSetting = new MqttTransportSettings(TransportType.Mqtt_Tcp_Only);
            ITransportSettings[] settings = { mqttSetting };

            // Open a connection to the Edge runtime
            ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
            await ioTHubModuleClient.OpenAsync();
            Console.WriteLine("IoT Hub module client initialized.");

            // Register callback to be called when a message is received by the module
            await ioTHubModuleClient.SetInputMessageHandlerAsync("input1", PipeMessage, ioTHubModuleClient);

            // Construct and configure GPIO controller/pins
            controller = new GpioController ();
            controller.OpenPin (GPIO_trigger, PinMode.Output);
            controller.OpenPin(GPIO_echo, PinMode.Input);
            Console.WriteLine("Pins opened");

            // Debug do something
            await SendDebugData(cts);
        }

        private static async Task SendDebugData(CancellationTokenSource cts) {
            while (!cts.Token.IsCancellationRequested) {
                controller.Write (GPIO_trigger, PinValue.High);
                await Task.Delay(1);
                controller.Write (GPIO_trigger, PinValue.Low);
                
                DateTime startTime = DateTime.Now;
                DateTime stopTime = DateTime.Now;

                while (controller.Read(GPIO_echo) == PinValue.Low) {
                    stopTime = DateTime.Now;
                }

                int timeElapsed = (stopTime - startTime).Milliseconds;
                float distance = (timeElapsed * 34300) / 2;

                string debugMessagePayload = $"Distance measurement = {distance}";
                Console.WriteLine($"Sending message: {debugMessagePayload}");
                Message debugMessage = new Message(Encoding.ASCII.GetBytes(debugMessagePayload));
                await ioTHubModuleClient.SendEventAsync(debugMessage);
                await Task.Delay(telemetryInterval, cts.Token);
            }
        }

        /// <summary>
        /// This method is called whenever the module is sent a message from the EdgeHub. 
        /// It just pipe the messages without any change.
        /// It prints all the incoming messages.
        /// </summary>
        static async Task<MessageResponse> PipeMessage(Message message, object userContext)
        {
            int counterValue = Interlocked.Increment(ref counter);

            var moduleClient = userContext as ModuleClient;
            if (moduleClient == null)
            {
                throw new InvalidOperationException("UserContext doesn't contain " + "expected values");
            }

            byte[] messageBytes = message.GetBytes();
            string messageString = Encoding.UTF8.GetString(messageBytes);
            Console.WriteLine($"Received message: {counterValue}, Body: [{messageString}]");

            if (!string.IsNullOrEmpty(messageString))
            {
                using (var pipeMessage = new Message(messageBytes))
                {
                    foreach (var prop in message.Properties)
                    {
                        pipeMessage.Properties.Add(prop.Key, prop.Value);
                    }
                    await moduleClient.SendEventAsync("output1", pipeMessage);
                
                    Console.WriteLine("Received message sent");
                }
            }
            return MessageResponse.Completed;
        }
    }
}
