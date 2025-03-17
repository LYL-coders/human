using LKZ.Commands.Voice;
using LKZ.DependencyInject;
using LKZ.TypeEventSystem;
using System;
using System.Collections;
using System.IO.Compression;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using UnityEngine;

namespace LKZ.Voice
{
    public sealed class VoiceRecognizerModel
    {
        const string url= "ws://1.94.131.28:19463/recognition";

        // 添加静态标志位，用于标记是否正在处理GPT响应
        public static bool isProcessingGPTResponse = false;

        [Inject]
        private MonoBehaviour _mono { get; set; }

        [Inject]
        private ISendCommand SendCommand { get; set; }

        [Inject]
        private IRegisterCommand RegisterCommand { get; set; }


        private VoiceRecognitionResultCommand voiceRecognitionResult = new VoiceRecognitionResultCommand();

        VoiceRecognizerBase voiceRecognizer;
        private string lastRecognizedText = "";
        private float lastRecognitionTime = 0f;
        private const float MIN_RECOGNITION_INTERVAL = 1.0f;

        public void Initialized()
        { 
            RegisterCommand.Register<SettingVoiceRecognitionCommand>(SettingVoiceRecognitionCommandCallback);

            voiceRecognizer = new VoiceRecognizerNoWebGL();
            voiceRecognizer.Initialized(this._mono, url, this.DisponseRecognition);
        }
         
        /// <summary>
        /// 设置语音识别命令回调
        /// </summary>
        /// <param name="obj"></param>
        private void SettingVoiceRecognitionCommandCallback(SettingVoiceRecognitionCommand obj)
        {
            voiceRecognizer.SetIsRecogition(obj.IsStartVoiceRecognition);
        }

        /// <summary>
        /// 设置是否正在处理GPT响应
        /// </summary>
        /// <param name="isProcessing">是否正在处理</param>
        public static void SetProcessingGPTResponse(bool isProcessing)
        {
            try
            {
                // 始终输出日志，无论状态是否变化
                Debug.Log($"设置GPT响应处理状态: {isProcessing}, 当前状态: {isProcessingGPTResponse}, 调用堆栈: {Environment.StackTrace}");
                
                // 更新状态
                isProcessingGPTResponse = isProcessing;
            }
            catch (Exception ex)
            {
                Debug.LogError($"设置GPT响应处理状态时出错: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 处理识别结果
        /// </summary>
        /// <param name="count"></param>
        private void DisponseRecognition(string text1)
        {  
            try
            {
                // 记录当前状态
                Debug.Log($"处理语音识别结果: {text1}, 当前GPT响应处理状态: {isProcessingGPTResponse}");
                
                // 如果正在处理GPT响应，跳过语音识别处理
                if (isProcessingGPTResponse)
                {
                    Debug.Log($"正在处理GPT响应，跳过语音识别: {text1}");
                    return;
                }

                if (text1 == " N" || text1 == "N" || text1 == "A" || text1 == " A")
                {
                    Debug.Log($"跳过特殊标记: {text1}");
                    return;
                }

                if (text1 == "\n")
                {
                    float timeSinceLastRecognition = Time.time - lastRecognitionTime;
                    Debug.Log($"收到换行符，距上次识别: {timeSinceLastRecognition}秒, 最小间隔: {MIN_RECOGNITION_INTERVAL}秒");
                    
                    if (timeSinceLastRecognition > MIN_RECOGNITION_INTERVAL)
                    {
                        voiceRecognitionResult.IsComplete = true;
                        voiceRecognitionResult.text = string.Empty;
                        lastRecognizedText = "";
                        lastRecognitionTime = Time.time;
                        Debug.Log("发送语音识别完成信号");
                        SendCommand.Send(voiceRecognitionResult);
                    }
                    else
                    {
                        Debug.Log($"忽略过快的结束信号，间隔: {timeSinceLastRecognition}秒");
                    }
                    return;
                }

                if (!string.IsNullOrEmpty(text1))
                {
                    float timeSinceLastRecognition = Time.time - lastRecognitionTime;
                    bool isDifferentText = text1 != lastRecognizedText;
                    bool isTimeIntervalSufficient = timeSinceLastRecognition > MIN_RECOGNITION_INTERVAL;
                    bool isNotContained = lastRecognizedText.Length > 0 && !text1.Contains(lastRecognizedText);
                    
                    Debug.Log($"检查语音识别条件: 不同文本={isDifferentText}, 时间间隔足够={isTimeIntervalSufficient}, 不包含上次文本={isNotContained}, 间隔={timeSinceLastRecognition}秒");
                    
                    if (isDifferentText && (isTimeIntervalSufficient || isNotContained))
                    {
                        voiceRecognitionResult.IsComplete = false;
                        voiceRecognitionResult.text = text1;
                        lastRecognizedText = text1;
                        lastRecognitionTime = Time.time;
                        Debug.Log($"发送新识别语音: {text1}");
                        SendCommand.Send(voiceRecognitionResult);
                    }
                    else
                    {
                        Debug.Log($"跳过重复或过快的语音识别: {text1}, 间隔: {timeSinceLastRecognition}秒");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"处理语音识别结果时出错: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public void OnDestroy()
        { 
        }
    }


    public abstract class VoiceRecognizerBase
    {
        public abstract void Initialized(MonoBehaviour _mono,string websocketUrl, Action<string> _recognizerCallback);

        public abstract void SetIsRecogition(bool IsRecogition);
    } 

#if UNITY_EDITOR ||UNITY_STANDALONE || UNITY_ANDROID

    public sealed class VoiceRecognizerNoWebGL : VoiceRecognizerBase
    {
        ClientWebSocket webSocket;

        AudioClip microphoneClip;


        /// <summary>
        /// ������˷���ʱ��
        /// </summary>
        WaitForSeconds samplingInterval = new WaitForSeconds(1 / 5f);

        private MonoBehaviour _mono;

        private Action<string> recognizerCallback;
        private bool IsRecogition;

        public override async void Initialized(MonoBehaviour _mono, string websocketUrl, Action<string> _recognizerCallback)
        {
            this._mono = _mono;
            recognizerCallback = _recognizerCallback;

            webSocket = new ClientWebSocket();
            await webSocket.ConnectAsync(new Uri(websocketUrl), default);

            _mono.StartCoroutine(InitializedMicrophone());
            byte[] p = new byte[1024 * 1024];
            int count = 0;
            while (true)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(p, count, p.Length - count), default);
                count += result.Count;
                if (result.EndOfMessage)
                {
                    var str = Encoding.UTF8.GetString(p, 0, count);
                    recognizerCallback?.Invoke(str);
                    count = 0;
                }
            }
        }

        public override void SetIsRecogition(bool IsRecogition)
        {
            this.IsRecogition = IsRecogition;
            if (IsRecogition)
                this.lastSampling = Microphone.GetPosition(null);

        }

        IEnumerator InitializedMicrophone()
        {
            yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
            if (Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                do
                {
                    microphoneClip = Microphone.Start(null, true, 1, 16000);
                    yield return null;
                } while (!Microphone.IsRecording(null));

                _mono.StartCoroutine(MicrophoneSamplingRecognition());
            }
            else
            {
                Debug.Log("����Ȩ��˷�Ȩ�ޣ�");
            }
        }


        /// <summary>
        /// ��һ�β���λ��
        /// </summary>
        int lastSampling;

        float[] f = new float[16000];
        IEnumerator MicrophoneSamplingRecognition()
        {
            while (true)
            {
                yield return samplingInterval;
                if (!IsRecogition)
                    continue;

                int currentPos = Microphone.GetPosition(null);
                bool isSucceed = microphoneClip.GetData(f, 0);

                if (isSucceed)
                    if (lastSampling != currentPos)
                    {
                        int count = 0;
                        float[] p = default;
                        if (currentPos > lastSampling)
                        {
                            count = currentPos - lastSampling;
                            p = new float[count]; 
                            Array.Copy(f, lastSampling, p, 0, count);
                        }
                        else
                        {
                            count = 16000 - lastSampling;
                            p = new float[count + currentPos]; 
                            Array.Copy(f, lastSampling, p, 0, count);
                             
                            Array.Copy(f, 0, p, count, currentPos);

                            count += currentPos;
                        }

                        lastSampling = currentPos;
                        DisponseRecognition(p);
                    }

            }
        }

        private void DisponseRecognition(float[] p)
        {
            var buffer = FloatArrayToByteArray(p);

            
            this.webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, default);
        }


        byte[] FloatArrayToByteArray(in float[] floatArray)
        {
            int byteCount = floatArray.Length * sizeof(float);
            byte[] byteArray = new byte[byteCount];

            Buffer.BlockCopy(floatArray, 0, byteArray, 0, byteCount);

            return byteArray;
        }

        static byte[] Compress(in byte[] data)
        {
            using (MemoryStream compressedStream = new MemoryStream())
            {
                using (GZipStream gzipStream = new GZipStream(compressedStream, CompressionMode.Compress))
                {
                    gzipStream.Write(data, 0, data.Length);
                }
                return compressedStream.ToArray();
            }
        }
    }
#endif
}