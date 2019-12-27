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

namespace SpeechRecogSample
{
    class Program
    {
        static SpeechConfig InitializeSpeechConfig(string endpoint, string subscriptionKey) =>
            SpeechConfig.FromEndpoint(new Uri(endpoint), subscriptionKey).Also(m =>
            {
                m.SpeechRecognitionLanguage = "ja-JP";
                m.OutputFormat = OutputFormat.Detailed;
            });

        static async Task SpeechContinuousRecognitionAsync(Options opts)
        {
            var config = InitializeSpeechConfig(opts.Endpoint, opts.SubscriptionKey);
            var resultList = new List<RecognitionResult>();
            var recognitionRunningSubject = new BehaviorSubject<bool>(false);
            var resultSubject = new Subject<RecognitionResult>();
            using var _ = resultSubject.Subscribe((r) =>
            {
                resultList.Add(r);
                Console.WriteLine($"{r.File}: {r.Result} ({r.Confidence}/1.0)");
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
