using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using CommandLine;
using CsvHelper;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SpeechRecogSample
{
    class Program
    {
        readonly static string InputFileName = "input.txt";
        readonly static string OutputFileName = "output.txt";
        readonly static string TempDirInDocker = "/tmp";

        static SpeechConfig InitializeSpeechConfig(string endpoint, string subscriptionKey) =>
            SpeechConfig.FromEndpoint(new Uri(endpoint), subscriptionKey).Also(m =>
            {
                m.SpeechRecognitionLanguage = "ja-JP";
                m.OutputFormat = OutputFormat.Detailed;
            });

        static async Task<IEnumerable<string>> GetAnalysisResultAsync(DockerClient client, string containerId, string mountDir, string text)
        {
            await File.WriteAllTextAsync(Path.Combine(mountDir, InputFileName), text, Encoding.UTF8);

            await client.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
            
            while (true)
            {
                var s = await client.Containers.InspectContainerAsync(containerId);
                if (!s.State.Running)
                {
                    break;
                }
                await Task.Delay(200);
            }

            return await File.ReadAllLinesAsync(Path.Combine(mountDir, OutputFileName), Encoding.UTF8);
        }

        static async Task SpeechContinuousRecognitionAsync(Options opts)
        {
            var tempDir = CreateRandomTempDirectory();
            var client = new DockerClientConfiguration().CreateClient();
            var createContainerResponse = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Cmd = new[] { "mecab", Path.Combine(TempDirInDocker, InputFileName), "-o", Path.Combine(TempDirInDocker, OutputFileName) },
                Image = "intimatemerger/mecab-ipadic-neologd",
                HostConfig = new HostConfig
                {
                    Mounts = new[]{new Mount
                    {
                        Source = tempDir,
                        Target = TempDirInDocker,
                        Type = "bind"
                    }}
                }
            });
            var containerId = createContainerResponse.ID;
            
            var config = InitializeSpeechConfig(opts.Endpoint, opts.SubscriptionKey);
            var resultList = new List<RecognitionResult>();
            var recognitionRunningSubject = new BehaviorSubject<bool>(false);
            var resultSubject = new Subject<RecognitionResult>();
            using var _ = resultSubject.Subscribe(async (r) =>
            {
                var result = await GetAnalysisResultAsync(client, containerId, tempDir, r.RawResult);
                if (result.Count() == 0)
                {
                    resultList.Add(r);
                    Console.WriteLine($"{r.File}: {r.Result} ({r.Confidence}/1.0)");
                    return;
                }
                var filteredText = result.Where(s => (s.Split('\t').Skip(1).FirstOrDefault() ?? "").Split(',').FirstOrDefault() != "感動詞")
                    .Where(s => !s.StartsWith("EOS"))
                    .Select(s => s.Split('\t').FirstOrDefault() ?? "").Aggregate((a, b) => a + b);
                var newResult = r.Also(a => {
                    a.Result = filteredText;
                });
                resultList.Add(newResult);
                Console.WriteLine($"{newResult.File}: {newResult.Result} ({newResult.Confidence}/1.0)");
            });

            foreach (var f in Directory.EnumerateFiles(Path.GetFullPath(opts.SourceDir), "*.wav")
                        .OrderBy((f) => Path.GetFileName(f)))
            {
                recognitionRunningSubject.OnNext(false);

                using var audioConfig = AudioConfig.FromWavFileInput(f);
                using var recognizer = new SpeechRecognizer(config, audioConfig).Also(r =>
                {
                    r.Recognized += (_, e) => e.Result.Also(result =>
                    {
                        if (result.Reason == ResultReason.RecognizedSpeech)
                        {
                            resultSubject.OnNext(result.Best().FirstOrDefault().Let(r => new RecognitionResult()
                            {
                                File = f,
                                Result = r.Text,
                                RawResult = r.Text,
                                Confidence = r.Confidence,
                            }));
                        }
                    });

                    r.SessionStopped += (_, __) => recognitionRunningSubject.OnNext(true);
                });

                await recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

                while (!recognitionRunningSubject.Value)
                {
                    await Task.Delay(200);
                }

                await recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(opts.Result))
            {
                return;
            }

            using var writer = new StreamWriter(opts.Result);
            using var csv = new CsvWriter(writer);
            csv.WriteRecords(resultList);

            await client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters());
        }

        static string CreateRandomTempDirectory()
        {
            var platformDependentMountableTempBaseDir = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "/tmp" : Path.GetTempPath();
            var randomTempDir = Path.GetFullPath(Path.Combine(platformDependentMountableTempBaseDir, Guid.NewGuid().ToString()));
            Directory.CreateDirectory(randomTempDir);
            return randomTempDir;
        }

        static void Main(string[] args)
        {
            CommandLine.Parser.Default.ParseArguments<Options>(args)
                .WithParsed(opts => SpeechContinuousRecognitionAsync(opts).Wait());
        }

        class Options
        {
            [Option("source-dir", Required = true, HelpText = "source wav files dir")]
            public string SourceDir { get; set; }

            [Option("endpoint", Required = true, HelpText = "get Azure Portal")]
            public string Endpoint { get; set; }

            [Option("subscription-key", Required = true, HelpText = "get Azure Portal")]
            public string SubscriptionKey { get; set; }

            [Option("result", HelpText = "result output file(csv)")]
            public string Result { get; set; }

        }

        class RecognitionResult
        {
            public string File { get; set; }
            public string Result { get; set; }
            public string RawResult { get; set; }
            public double Confidence { get; set; }
        }
    }

    // https://dev.to/amay077/kotlins-scope-functions-in-c-pbn
    static class ObjectExtensions
    {
        // Kotlin: fun <T, R> T.let(block: (T) -> R): R
        public static R Let<T, R>(this T self, Func<T, R> block)
        {
            return block(self);
        }

        // Kotlin: fun <T> T.also(block: (T) -> Unit): T
        public static T Also<T>(this T self, Action<T> block)
        {
            block(self);
            return self;
        }
    }
}
