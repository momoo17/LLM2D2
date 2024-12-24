using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using MikeSchweitzer.WebSocket;
using TMPro;
using UniRealtime.OpenAI.Request;
using UniRealtime.Response;
using UnityEngine;
using UnityEngine.Networking;

namespace UniRealtime.Sample
{
    /// <summary>
    /// サンプルクラス
    /// </summary>
    public class Sample : MonoBehaviour
    {
        [SerializeField] private string _apiKey;

        /// <summary>
        /// レスポンスを表示するTextMeshProUGUI
        /// </summary>
        //[SerializeField] private TextMeshProUGUI responseText;
        private string responseText;

        /// <summary>
        /// 入力した音声を表示するTextMeshProUGUI
        /// </summary>
        //[SerializeField] private TextMeshProUGUI inputText;
        private string inputText;

        /// <summary>
        /// 音声の選択
        /// </summary>
        [SerializeField] private Voice _selectVoice;

        /// <summary>
        /// 音声を再生するAudioSource
        /// </summary>
        [SerializeField] private AudioSource audioSource;

        /// <summary>
        /// OpenAI Realtimeに関するクラス
        /// </summary>
        private OpenAIRealtimeClient _openAIRealtimeClient;

        /// <summary>
        /// マイクから取得した最後のサンプル位置
        /// </summary>
        private int _lastSamplePosition = 0;

        /// <summary>
        /// 音声データのバッファを float 型のスレッドセーフなキューに変更
        /// </summary>　
        private readonly ConcurrentQueue<float> _audioBuffer = new ConcurrentQueue<float>();

        /// <summary>
        /// 音声の入力に関するクラス
        /// </summary>
        private AudioRecorder _audioRecorder;

        /// <summary>
        /// CancellationTokenSource
        /// </summary>
        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// WebSocketConnection
        /// </summary>
        [SerializeField] private WebSocketConnection webSocketConnection;

        /// <summary>
        /// momo
        /// </summary>
        private readonly string baseFolderPath = Path.Combine(Application.streamingAssetsPath, "Sounds6");
        private readonly string[] emotionClass = { "Happiness", "Sadness", "Disgust", "Fear", "Surprise", "Anger"}; 
        private readonly string[] intentionClass = { "Acknowledgement", "Affirmation", "Negation", "Question", "Unsure"}; 

        private void Awake()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _openAIRealtimeClient = new OpenAIRealtimeClient(webSocketConnection, _apiKey);
            _audioRecorder = new AudioRecorder();
            _openAIRealtimeClient.OnMessageReceivedEvent += HandleMessageReceived;
        }


        /// <summary>
        /// 開始処理
        /// </summary>
        private async void Start()
        {
            // マイクの設定
            audioSource.loop = true;
            audioSource.Play();

            // Realtime APIに接続
            await _openAIRealtimeClient.ConnectToRealtimeAPI(_cancellationTokenSource.Token);

            // 入力した音声の文字起こし情報も取得する場合
            var sessionUpdateMessage = new SessionUpdateMessage
            {
                EventId = "",
                Type = "session.update",
                Session = new SessionDetails
                {
                    //Modalities = new List<string> { Modalities.Text.ToString().ToLower(), Modalities.Audio.ToString().ToLower() },
                    Modalities = new List<string> { Modalities.Text.ToString().ToLower()},
                    
                    // intention
                    // Instructions = "あなたは優秀な対話アシスタントです。私が話しかけた内容に対して、短い応答を作成してください。妥当な応答意図を Affirmation, Negation, Unsure, Question, Acknowledgement のいずれかに分類してください。出力形式は以下に従ってください:「返答, 意図分類」。それ以外の情報や形式は出力しないでください。",
                    
                    // emotion + intention
                    Instructions = "あなたは共感的で優秀なバーチャルエージェントです。これから意図伝達クイズを行います。このクイズでは、ユーザーがあなたから正解を引き出そうと話しかけます。あなたは事前に次の5つの質問とその正解を知っています：「明日の天気」（正解：雨）、「次の角で進む方向」（正解：右）、「次の目的地」（正解：図書館）、「この先の道路の交通情報」（正解：空いている）、「家の電気の点灯状態」（正解：消灯している）。あなたの目的は、応答感情と応答意図を通じてユーザーを正解へ導くことです。応答感情は Happiness, Sadness, Fear, Surprise, Anger, Disgust の中から選び、応答意図は Affirmation, Negation, Unsure, Question, Acknowledgement の中から選択してください。なお、Questionはユーザーの意図が明確に理解できなかった時のみ選択してください。出力形式は以下に従い、選択結果のみを記載してください：「感情, 意図」。クイズの進行中は、事前に知っている正解をユーザーに直接伝えるのではなく、感情と意図を使って適切にヒントを与え、正解へと導いてください。クイズの進行以外の情報や説明は出力しないでください。",

                    Voice = _selectVoice,
                    // pcm16, g711_ulaw, or g711_alaw
                    InputAudioFormat = AudioFormat.PCM16,
                    // pcm16, g711_ulaw, or g711_alaw
                    OutputAudioFormat = AudioFormat.PCM16,
                    InputAudioTranscription = new InputAudioTranscription
                    {
                        Model = "whisper-1"
                    },
                    TurnDetection = new TurnDetection
                    {
                        Type = "server_vad",
                        Threshold = 0.5,
                        PrefixPaddingMs = 300,
                        SilenceDurationMs = 500
                    },
                    Tools = new List<Tool>
                    {
                        new Tool
                        {
                            Type = "function",
                            Name = "get_weather",
                            Description = "Get the current weather for a location, tell the user you are fetching the weather.",
                            Parameters = new ToolParameters
                            {
                                Type = "object",
                                Properties = new Dictionary<string, ToolProperty>
                                {
                                    { "location", new ToolProperty { Type = "string" } }
                                },
                                Required = new List<string> { "location" }
                            }
                        }
                    },
                    ToolChoice = "auto",
                    Temperature = 0.8,
                    MaxResponseOutputTokens = "inf"
                }
            };
            _openAIRealtimeClient.SendSessionUpdate(sessionUpdateMessage);
        }

        /// <summary>
        /// 更新処理
        /// </summary>
        private void Update()
        {
            // 接続が確立されるまで音声データの送信を停止
            if (!_openAIRealtimeClient.IsConnected)
            {
                return;
            }

#if UNITY_WEBGL
            // WebGLのマイク入力の実装
#else
            // マイクから音声データを取得して送信
            if (Microphone.IsRecording(_audioRecorder.Microphone))
            {
                int currentPosition = Microphone.GetPosition(_audioRecorder.Microphone);

                if (currentPosition < _lastSamplePosition)
                {
                    // ループした場合
                    _lastSamplePosition = 0;
                }

                int sampleLength = currentPosition - _lastSamplePosition;

                if (sampleLength > 0)
                {
                    float[] samples = new float[sampleLength];
                    _audioRecorder.AudioClip.GetData(samples, _lastSamplePosition);

                    // 更新
                    _lastSamplePosition = currentPosition;

                    // 音声データを送信
                    _openAIRealtimeClient.SendAudioData(samples);
                }
            }
#endif
        }

        /// <summary>
        /// 音声データを取得するためのメソッド
        /// </summary>
        /// <param name="data"></param>
        /// <param name="channels"></param>
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (_audioBuffer == null) return;

            for (int i = 0; i < data.Length; i += channels)
            {
                float sample;
                if (_audioBuffer.TryDequeue(out sample))
                {
                    data[i] = sample;

                    // ステレオ対応
                    if (channels == 2)
                    {
                        data[i + 1] = sample;
                    }
                }
                /*
                else
                {
                    // バッファが空の場合は無音にする
                    data[i] = 0;

                    if (channels == 2)
                    {
                        data[i + 1] = 0;
                    }
                }*/
            }
        }

        /// <summary>
        /// メッセージを受信したときに呼び出されるメソッド
        /// </summary>
        /// <param name="response"></param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        private void HandleMessageReceived(RealtimeResponse response)
        {
            switch (response.ResponseType)
            {
                case ResponseType.SessionCreated: break;
                case ResponseType.SessionUpdated: break;
                case ResponseType.ResponseCreated: break;
                case ResponseType.RateLimitsUpdated: break;
                case ResponseType.ConversationItemCreated: break;
                case ResponseType.ResponseOutputItemAdded: break;
                case ResponseType.ResponseOutputItemDone: break;
                case ResponseType.ResponseTextDelta: break;
                case ResponseType.ResponseTextDone:
                    if (!string.IsNullOrEmpty(response.Text))
                    {
                        responseText = response.Text;
                        Debug.Log($"response text: {responseText}");

                        // テキスト部分を削除してカテゴリのみを抽出
                        string[] parts = responseText.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                        // 感情と意図の分類
                        string emotion = null;
                        string intent = null;

                        foreach (string part in parts)
                        {
                            if (Array.Exists(emotionClass, e => e.Equals(part.Trim(), StringComparison.OrdinalIgnoreCase)))
                            {
                                emotion = part.Trim();
                            }
                            else if (Array.Exists(intentionClass, i => i.Equals(part.Trim(), StringComparison.OrdinalIgnoreCase)))
                            {
                                intent = part.Trim();
                            }
                        }

                         // カテゴリ配列の生成（感情と意図の順に追加）
                        List<string> categories = new List<string>();
                        if (!string.IsNullOrEmpty(emotion)) categories.Add(emotion);
                        if (!string.IsNullOrEmpty(intent)) categories.Add(intent);

                        Debug.Log($"categories: {string.Join(", ", categories)}");

                        if (categories.Count > 0)
                        {
                            // 抽出したカテゴリ順に音声を再生
                            StartCoroutine(PlayRandomSoundSequentially(categories));
                        }
                        else
                        {
                            Debug.LogError("No valid categories found in response.");
                        }
                    }
                    break;
                case ResponseType.ResponseAudioTranscriptDelta:
                    if (!string.IsNullOrEmpty(response.Text))   
                    {
                        //responseText.text += response.Text;
                    }
                    break;
                case ResponseType.ResponseAudioTranscriptDone:
                    if (!string.IsNullOrEmpty(response.Text))
                    {
                        //responseText.text = response.Text;
                    }
                    break;
                case ResponseType.ResponseAudioDelta:
                    // 受信した音声データ（PCM16 データ）をデコードしてバッファに追加
                    byte[] pcmData = response.AudioData;

                    // 入力サンプル数を計算
                    int inputSampleCount = pcmData.Length / 2;
                    float[] inputSamples = new float[inputSampleCount];

                    // バイト配列をfloat配列に変換（正規化）
                    for (int i = 0; i < inputSampleCount; i++)
                    {
                        // リトルエンディアンの場合
                        short sample = BitConverter.ToInt16(pcmData, i * 2);

                        // short の最大値で割って -1.0f ～ 1.0f に正規化
                        inputSamples[i] = sample / (float)short.MaxValue;
                    }

                    // Unity のサンプリングレートを取得
                    int unitySampleRate = AudioSettings.outputSampleRate;

                    // 入力データのサンプリングレートに合わせてリサンプリング
                    // DOCS: https://platform.openai.com/docs/guides/realtime#audio-formats
                    // raw 16 bit PCM audio at 24kHz, 1 channel, little-endian
                    int inputSampleRate = 24000;

                    // リサンプリングの比率を計算
                    float resampleRatio = (float)unitySampleRate / inputSampleRate;

                    // リサンプリングを行う
                    float[] resampledSamples = AudioUtility.ResampleAudio(inputSamples, resampleRatio);

                    // バッファに追加
                    foreach (var sample in resampledSamples)
                    {
                        _audioBuffer.Enqueue(sample);
                    }
                    break;
                case ResponseType.ResponseAudioDone:
                    // 何もしない
                    break;
                case ResponseType.ResponseDone: break;
                case ResponseType.InputAudioBufferSpeechStarted: break;
                case ResponseType.InputAudioBufferSpeechStopped:
                    responseText = string.Empty;
                    break;
                case ResponseType.InputAudioBufferCommitted: break;
                case ResponseType.Error: break;
                case ResponseType.Unknown: break;
                case ResponseType.InputAudioTranscriptPartial:
                    if (!string.IsNullOrEmpty(response.Text))
                    {
                        inputText += response.Text;
                    }
                    break;
                case ResponseType.InputAudioTranscriptDone:
                    if (!string.IsNullOrEmpty(response.Text))
                    {
                        inputText = response.Text;
                    }
                    break;
                case ResponseType.ConversationItemInputAudioTranscriptionCompleted:
                    // ユーザーの音声入力の転写が完了した際の処理
                    if (!string.IsNullOrEmpty(response.Text))
                    {
                        inputText = response.Text;
                        Debug.Log($"input text: {inputText}");
                    }
                    break;
                case ResponseType.ResponseContentPartAdded:
                case ResponseType.ResponseContentPartDone:
                    break;
                default:
                    Debug.Log("Unknown ResponseType: " + response.ResponseType);
                    break;
            }
        }

        private System.Collections.IEnumerator PlayRandomSoundSequentially(List<string> categories)
        {
            foreach (string category in categories)
            {
                // カテゴリに対応する音声を再生して完了を待機
                yield return PlayRandomSound(category);
            }
        }

        private System.Collections.IEnumerator PlayRandomSound(string classification)
        {
            // フォルダのパスを設定
            string folderPath;
            if (Array.Exists(emotionClass, e => e.Equals(classification, StringComparison.OrdinalIgnoreCase)))
            {
                folderPath = Path.Combine(baseFolderPath, "Emotion", classification);
            }
            else if (Array.Exists(intentionClass, i => i.Equals(classification, StringComparison.OrdinalIgnoreCase)))
            {
                folderPath = Path.Combine(baseFolderPath, "Intention", classification);
            }
            else
            {
                Debug.LogError($"Invalid classification: {classification}");
                yield break;
            }

            // フォルダが存在するか確認
            if (!Directory.Exists(folderPath))
            {
                Debug.LogError($"Folder not found: {folderPath}");
                yield break;
            }

            // フォルダ内の音声ファイル（.wav と .mp3）を取得
            List<string> audioFiles = new List<string>();
            audioFiles.AddRange(Directory.GetFiles(folderPath, "*.wav"));
            audioFiles.AddRange(Directory.GetFiles(folderPath, "*.mp3"));

            if (audioFiles.Count == 0)
            {
                Debug.LogError($"No audio files found in folder: {folderPath}");
                yield break;
            }

            // ランダムに音声ファイルを選択
            string randomFilePath = audioFiles[UnityEngine.Random.Range(0, audioFiles.Count)];
            string fullPath = "file://" + randomFilePath;

            //Debug.Log($"Attempting to load audio from: {fullPath}");

            // ファイル拡張子に応じた AudioType を設定
            AudioType audioType = randomFilePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                ? AudioType.WAV
                : AudioType.MPEG;


            // 音声をロードして再生
            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(fullPath, audioType))
            {
                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError)
                {
                    Debug.LogError($"Failed to load audio: {www.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                if (clip != null)
                {
                    Debug.Log($"Playing audio: {classification}");
                    audioSource.PlayOneShot(clip);

                    // 音声が再生される時間を待機
                    yield return new WaitForSeconds(clip.length);
                }
                else
                {
                    Debug.LogError("AudioClip is null!");
                }
            }
        }

        /// <summary>
        /// 破棄処理
        /// </summary>
        private void OnDestroy()
        {
            // イベントの解除とクライアントの破棄
            _openAIRealtimeClient.OnMessageReceivedEvent -= HandleMessageReceived;
            _openAIRealtimeClient.Dispose();

            // cancellationTokenSource を破棄
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
        }
    }
}
