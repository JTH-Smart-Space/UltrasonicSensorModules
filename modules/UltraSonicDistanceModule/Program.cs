namespace UltraSonicDistanceModule
{
    using System;
    using System.Device.Gpio;
    using System.Diagnostics;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Runtime.Loader;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    using Microsoft.Azure.Devices.Shared;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    class Program
    {

        public class Observation {
            public DateTime observationTime {get; set;}
            [JsonProperty("value")]
            public double? numericValue {get; set;}
            [JsonProperty("valueString")]
            public string stringValue {get; set;}
            [JsonProperty("valueBoolean")]
            public bool? booleanValue {get; set;}
            public string sensorId {get; set;}
	    }
	
        public class RecEdgeMessage {
            public string format {get {return "rec3.2";}}
            public string deviceId {get; set;}
            public List<Observation> observations { get; set; }
        }

        public class UltrasonicSensor 
        {
            // GPIO pin on which outbound ultrasonic burst is emitted
            public int GpioTrigger {get; set;}
            // GPIO pin listening for the return echo signal
            public int GpioEcho {get; set;}
            // Distance in meters below which a detection is reported
            public double SensingDistance {get; set;}
            public string SensorId {get; set;}
        }

        private static string ioTEdgeDeviceId = Environment.GetEnvironmentVariable("IOTEDGE_DEVICEID");

        private static List<UltrasonicSensor> sensors = new List<UltrasonicSensor>();

        static int counter;

        private static CancellationTokenSource cts;

        // TODO: Make this configurable via Module Twin. Defaults to once every minute.
        private static TimeSpan telemetryInterval = new TimeSpan(0, 1, 0);

        private static ModuleClient ioTHubModuleClient;

        static GpioController controller = new GpioController ();

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

            // Register desired property callback
            await ioTHubModuleClient.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertyChanged, null);

            // Configure the ultrasonic sensors included in the module twin
            Twin moduleTwin = await ioTHubModuleClient.GetTwinAsync();
            ConfigureSensors(moduleTwin.Properties.Desired);

            // Start sending telemetry
            await SendTelemetry(cts);
        }

        private static long MilliSecsToTicks(double millisecs) {
            long frequency = Stopwatch.Frequency;
            double seconds = millisecs / 1000.0;
            long ticks = (long)(seconds * frequency);
            return ticks;
        }

        private static double TicksToMillisecs(long ticks) {
            long frequency = Stopwatch.Frequency;
            double seconds = (double)ticks / (double)frequency;
            double milliseconds = seconds * 1000.0;
            return milliseconds;
        }

        private static async Task OnDesiredPropertyChanged(TwinCollection desiredProperties, object userContext) {
            await Task.Run(()=>ConfigureSensors(desiredProperties));
        }

        static void ConfigureSensors(TwinCollection desiredProperties) {
            if (desiredProperties.Contains("ultrasonicSensors")) {

                // Close and clear old sensors
                foreach (UltrasonicSensor oldSensor in sensors) {
                    controller.ClosePin(oldSensor.GpioTrigger);
                    controller.ClosePin(oldSensor.GpioEcho);
                }
                sensors.Clear();

                // Parse and add new sensors
                JArray moduleTwinSensors = desiredProperties["ultrasonicSensors"];
                foreach (JObject sensor in moduleTwinSensors.Children()) {
                    string moduleTwinSensorId = sensor.GetValue("id").ToString();
                    int moduleTwinGpioTrigger = sensor.GetValue("trigger").ToObject<int>();
                    int moduleTwinGpioEcho = sensor.GetValue("echo").ToObject<int>();
                    float moduleTwinSensingDistance = sensor.GetValue("sensingDistance").ToObject<float>();
                    sensors.Add(new UltrasonicSensor(){SensorId = moduleTwinSensorId, GpioTrigger = moduleTwinGpioTrigger, GpioEcho = moduleTwinGpioEcho, SensingDistance = moduleTwinSensingDistance});
                    Console.WriteLine($"Configured sensor ID = {moduleTwinSensorId} with trigger = {moduleTwinGpioTrigger}, echo = {moduleTwinGpioEcho}, distance = {moduleTwinSensingDistance}.");
                    controller.OpenPin(moduleTwinGpioTrigger, PinMode.Output);
                    controller.OpenPin(moduleTwinGpioEcho, PinMode.Input);
                    Console.WriteLine("Opened sensor pins.");
                }
            }
        }

        private static async Task SendTelemetry(CancellationTokenSource cts) {
            while (!cts.Token.IsCancellationRequested) {

                // REC edge message holder (reused but observations cleared on each iteration)
                RecEdgeMessage recEdgeMessage = new RecEdgeMessage() { 
                    //deviceId = ioTEdgeDeviceId,
                    observations = new List<Observation>()
                };
                foreach(UltrasonicSensor sensor in sensors) {

                    // Transmit ultrasonic signal burst
                    controller.Write (sensor.GpioTrigger, PinValue.High);
                    Stopwatch delay = Stopwatch.StartNew();
                    // Transmitting for 0.01 millisecs
                    while (TicksToMillisecs(delay.ElapsedTicks) < 0.01) {
                    }
                    delay.Stop();
                    controller.Write (sensor.GpioTrigger, PinValue.Low);

                    // Measure the response time
                    Stopwatch timer = Stopwatch.StartNew();
                    while (controller.Read(sensor.GpioEcho) == PinValue.Low) {
                    }
                    timer.Stop();
                    double millisecsTaken = TicksToMillisecs(timer.ElapsedTicks);

                    // Distance computation: speed of sound is 0.343 m/millisecond. 
                    // Divide by 2 to account for sound going both ways (it's an echo)
                    double distance = (millisecsTaken * 0.343) / 2.0;
                    Console.WriteLine($"Detected distance: {distance} meters.");

                    // Set up observation
                    Observation presenceObservation = new Observation();
                    presenceObservation.observationTime = DateTime.Now;
                    presenceObservation.sensorId = sensor.SensorId;

                    // If we see something within the configured sensing distane, 
                    // report a motion, otherwise report no motion.
                    presenceObservation.booleanValue = distance < sensor.SensingDistance;
                    recEdgeMessage.observations.Add(presenceObservation);
                }

                // If there are any observations to report, send to IoTHub
                if (recEdgeMessage.observations.Count > 0) {
                    JsonSerializerSettings serializerSettings = new JsonSerializerSettings {
                        NullValueHandling = NullValueHandling.Ignore
                    };
                    string recEdgeMessageJson = JsonConvert.SerializeObject(recEdgeMessage, Formatting.None, serializerSettings);
                    Message telemetryMessage = new Message(Encoding.ASCII.GetBytes(recEdgeMessageJson)) {
                        ContentType = "application/json",
                        ContentEncoding = "utf-8"
                    };
                    Console.WriteLine($"Sending message: {recEdgeMessageJson}");
                    await ioTHubModuleClient.SendEventAsync(telemetryMessage);
                }

                // Wait predetermined time interval
                await Task.Delay(telemetryInterval, cts.Token);
                recEdgeMessage.observations.Clear();
            }
        }
    }
}
