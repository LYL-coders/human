using LKZ.Chat.Commands;
using LKZ.Commands.Chat;
using LKZ.Commands.Voice;
using LKZ.DependencyInject;
using LKZ.GPT;
using LKZ.Models;
using LKZ.TypeEventSystem;
using LKZ.VoiceSynthesis;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace LKZ.Logics
{

    public sealed class LLMLogic
    {

        private sealed class ResultData : IEnumerator
        {
            public string result;
            public IEnumerator clip;

            public object Current => current;

            object current = null;

            public bool MoveNext()
            {
                if (!(clip.Current is AudioClip))
                {
                    current = clip.Current;
                    return true;
                }
                else if (clip.Current is string str)
                    return str != VoiceTTS.ErrorMess;
                else
                    return false;
            }

            public void Reset()
            {

            }
        }

        [Inject]
        private AudioModel audioModel { get; set; }

        [Inject]
        private MonoBehaviour _mono { get; set; }

        [Inject]
        private ISendCommand SendCommand { get; set; }

        [Inject]
        private IRegisterCommand RegisterCommand { get; set; }

        /// <summary>
        /// GPT播放语音片段
        /// </summary>
        Queue<ResultData> gptVoice = new Queue<ResultData>();

        Action<string> _showUITextAction;

        // 添加单独的用户输入框和GPT回复框更新函数
        private Action<string> _userTextUpdateAction;
        private Action<string> _gptTextUpdateAction;

        private string onceResult;
        
        // 添加标志位，跟踪当前正在处理的语音识别文本
        private string currentProcessingVoiceText = "";
        private bool isDisplayingVoiceText = false;

        /// <summary>
        /// 是否接收完成GPT的内容
        /// </summary>
        private bool isRequestChatGPTContent, isStopCreate;
        
        /// <summary>
        /// 是否启用语音合成功能
        /// </summary>
        private bool enableVoiceSynthesis = false; // 默认关闭语音合成

        /// <summary>
        /// 字幕同步携程
        /// </summary>
        private Coroutine _titleSynchronization_Cor;

        /// <summary>
        /// 字幕同步携程
        /// </summary>
        private Coroutine _requestGPTSegmentationCor;

        private string currentGPTResponseText = ""; // 添加变量记录当前 GPT 响应文本
        private int responseCounter = 0; // 添加计数器跟踪响应处理次数
        private float lastResponseTime = 0f; // 记录上次响应时间
        private const float MIN_RESPONSE_INTERVAL = 0.5f; // 最小响应间隔(秒)
        private HashSet<string> processedResponses = new HashSet<string>(); // 用于跟踪已处理的响应
        private bool hasChatGPTContentAdded = false; // 标记是否已经添加了ChatGPT内容框
        private bool hasUserContentAdded = false; // 标记是否已经添加了用户内容框

        private string _currentConversationId = null; // 添加当前对话的唯一标识

        public void Initialized()
        {
            // 确保所有字符串变量都已初始化
            onceResult = string.Empty;
            currentProcessingVoiceText = string.Empty;
            currentGPTResponseText = string.Empty;
            _currentConversationId = null;
            
            // 确保集合类型已初始化
            if (processedResponses == null)
            {
                processedResponses = new HashSet<string>();
            }
            else
            {
                processedResponses.Clear();
            }
            
            // 默认关闭语音合成
            enableVoiceSynthesis = false;
            Debug.Log("初始化完成：语音合成功能已禁用，仅使用文本更新");
            
            RegisterCommand.Register<VoiceRecognitionResultCommand>(VoiceRecognitionResultCommandCallback);
            
            RegisterCommand.Register<StopGenerateCommand>(StopGenerateCommandCallback);
        }
         
        private void StopGenerateCommandCallback(StopGenerateCommand obj)
        {
            if (!object.ReferenceEquals(null, _titleSynchronization_Cor))
                _mono.StopCoroutine(_titleSynchronization_Cor);
            if (!object.ReferenceEquals(null, _requestGPTSegmentationCor))
                _mono.StopCoroutine(_requestGPTSegmentationCor);

            _titleSynchronization_Cor = null;
            _requestGPTSegmentationCor = null;

            PlayFinish();
        }

        /// <summary>
        /// 语音识别到内容回调
        /// </summary>
        /// <param name="obj"></param>
        private void VoiceRecognitionResultCommandCallback(VoiceRecognitionResultCommand obj)
        {
            try
            {
                // 首先确保obj不为空
                if (object.ReferenceEquals(obj, null))
                {
                    Debug.LogWarning("语音识别回调参数为空");
                    return;
                }

                // 检查是否是同一段语音文本的重复识别
                if (!obj.IsComplete)
                {
                    // 确保obj.text不为空
                    string recognizedText = obj.text ?? string.Empty;
                    
                    // 如果文本相同且正在显示中，则跳过
                    if (currentProcessingVoiceText == recognizedText && isDisplayingVoiceText)
                    {
                        Debug.Log($"跳过重复的语音识别文本显示: {recognizedText}");
                        return;
                    }
                    
                    currentProcessingVoiceText = recognizedText;
                    isDisplayingVoiceText = true;
                    
                    // 确保只创建一次用户内容框
                    if (_userTextUpdateAction == null && !hasUserContentAdded)
                    {
                        hasUserContentAdded = true;
                        Debug.Log("创建用户内容框");
                        
                        // 使用聊天管理器创建用户对话框
                        LLMChatManager.Instance.CreateOrGetDialog(Enum.InfoType.My, null);
                        _userTextUpdateAction = text => LLMChatManager.Instance.UpdateDialogText(Enum.InfoType.My, text);
                    }

                    // 确保onceResult已初始化
                    if (onceResult == null)
                    {
                        onceResult = string.Empty;
                    }

                    // 更新用户输入累积文本并显示
                    if (!string.IsNullOrEmpty(recognizedText))
                    {
                        // 安全地检查文本是否已包含在累积结果中
                        bool isTextAlreadyIncluded = false;
                        try
                        {
                            isTextAlreadyIncluded = onceResult.Contains(recognizedText);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"检查文本包含关系时出错: {ex.Message}");
                            isTextAlreadyIncluded = false;
                        }
                        
                        if (!isTextAlreadyIncluded)
                        {
                            onceResult += recognizedText;
                            
                            if (_userTextUpdateAction != null)
                            {
                                _userTextUpdateAction.Invoke(onceResult);
                                Debug.Log($"显示累积语音识别文本: {onceResult}");
                            }
                        }
                        else
                        {
                            Debug.Log($"跳过重复添加的语音文本: {recognizedText}");
                        }
                    }
                    
                    isDisplayingVoiceText = false;
            }
            else
            {
                    // 确保onceResult已初始化且不为空
                if (string.IsNullOrEmpty(onceResult))
                    {
                        Debug.Log("语音识别完成，但没有可用的文本内容");
                    return;
                    }
                    
                    currentProcessingVoiceText = "";
                    isDisplayingVoiceText = false;
                    // 重置用户内容框标志位，为下一轮对话做准备
                    hasUserContentAdded = false;

                SendCommand.Send(new SettingVoiceRecognitionCommand { IsStartVoiceRecognition = false });//停止语音识别

                    // 重置上一次对话的状态
                    _currentConversationId = Guid.NewGuid().ToString(); // 生成新对话ID
                    currentGPTResponseText = "";
                    responseCounter = 0;
                    lastResponseTime = 0f;
                    processedResponses.Clear();
                    
                    // 确保只创建一次ChatGPT内容框
                    hasChatGPTContentAdded = true;
                    // 不再重置_showUITextAction，而是使用专门的GPT回复框更新函数
                    Debug.Log($"创建ChatGPT内容框, 对话ID: {_currentConversationId}");
                    
                    // 使用聊天管理器创建GPT对话框
                    LLMChatManager.Instance.CreateOrGetDialog(Enum.InfoType.ChatGPT, null);
                    _gptTextUpdateAction = text => LLMChatManager.Instance.UpdateDialogText(Enum.InfoType.ChatGPT, text);
                    
                    // 设置当前活跃的文本更新函数为GPT回复框更新函数
                    _showUITextAction = _gptTextUpdateAction;

                ClearGPTVoice();

                    string textToSend = onceResult; // 保存一份副本
                    _requestGPTSegmentationCor = _mono.StartCoroutine(LKZ.GPT.LLM.Request(textToSend, ChatGPTRequestCallback));
                    onceResult = string.Empty; // 清空累积结果
                isStopCreate = false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"语音识别回调处理出错: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void ChatGPTRequestCallback(string arg1, bool arg2)
        {
            bool flagWasSet = false;
            try 
            {
                // 设置标志位，表示正在处理GPT响应
                Debug.Log($"LLMLogic.ChatGPTRequestCallback: 准备设置GPT响应处理状态: True, 是否最终回调: {arg2}");
                LKZ.Voice.VoiceRecognizerModel.SetProcessingGPTResponse(true);
                flagWasSet = true;
                
                if (string.IsNullOrEmpty(arg1))
                {
                    isRequestChatGPTContent = arg2;
                    return;
                }

                // 过滤掉<think>内容
                string filteredText = FilterThinkContent(arg1);
                if (string.IsNullOrEmpty(filteredText))
                {
                    // 如果过滤后内容为空，但这是最终回调，则使用默认消息
                    if (arg2 && responseCounter == 0)
                    {
                        filteredText = "你好！有什么我可以帮助你的吗？";
                        Debug.Log($"过滤后内容为空且是最终回调，使用默认消息: {filteredText}");
                    }
                    else
                    {
                        // 否则跳过这次回调
                        isRequestChatGPTContent = arg2;
                        return;
                    }
                }

                // 检查是否已经处理过这段文本，避免重复
                if (processedResponses.Contains(filteredText))
                {
                    Debug.Log($"跳过重复的响应文本: {(filteredText.Length > 10 ? filteredText.Substring(0, 10) + "..." : filteredText)}");
                    
                    // 仍然更新完成状态
                    isRequestChatGPTContent = arg2;
                    return;
                }
                
                // 将文本添加到已处理集合中
                processedResponses.Add(filteredText);

                // 确保有效的会话ID
                if (string.IsNullOrEmpty(_currentConversationId))
                {
                    _currentConversationId = Guid.NewGuid().ToString();
                    Debug.Log($"生成新的会话ID: {_currentConversationId}");
                }

                float currentTime = Time.time;
                string responseFirstChars = filteredText.Length > 10 ? filteredText.Substring(0, 10) : filteredText;
                Debug.Log($"收到 GPT 流式响应片段: {responseFirstChars}... (距上次: {currentTime - lastResponseTime}秒), 对话ID: {_currentConversationId}");
                
                lastResponseTime = currentTime;
                responseCounter++;
                
                // 确保GPT回复框更新函数已创建
                if (_gptTextUpdateAction == null)
                {
                    Debug.LogWarning("GPT回复框更新函数为空，重新创建");
                    // 使用聊天管理器创建GPT对话框
                    LLMChatManager.Instance.CreateOrGetDialog(Enum.InfoType.ChatGPT, null);
                    _gptTextUpdateAction = text => LLMChatManager.Instance.UpdateDialogText(Enum.InfoType.ChatGPT, text);
                    hasChatGPTContentAdded = true;
                }
                
                // 由于文本更新会在TitleSynchronizationCoroutine中处理，这里不再直接更新UI
                // 只创建语音合成
                _mono.StartCoroutine(SynthesisCoroutine(filteredText));
             
            isRequestChatGPTContent = arg2;
            }
            catch (Exception ex)
            {
                Debug.LogError($"ChatGPT回调处理出错: {ex.Message}\n{ex.StackTrace}");
                isRequestChatGPTContent = arg2; // 确保状态被正确设置
            }
            finally
            {
                // 如果是最终回调，重置标志位
                if (arg2)
                {
                    if (flagWasSet)
                    {
                        Debug.Log($"LLMLogic.ChatGPTRequestCallback: 准备重置GPT响应处理状态: False (最终回调)");
                        try
                        {
                            LKZ.Voice.VoiceRecognizerModel.SetProcessingGPTResponse(false);
                            Debug.Log("LLMLogic.ChatGPTRequestCallback: 已重置GPT响应处理状态 (最终回调)");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"LLMLogic.ChatGPTRequestCallback: 重置GPT响应处理状态时出错: {ex.Message}\n{ex.StackTrace}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning("LLMLogic.ChatGPTRequestCallback: 标志位未设置，跳过重置操作 (最终回调)");
                    }
                }
                else
                {
                    Debug.Log($"LLMLogic.ChatGPTRequestCallback: 非最终回调，保持GPT响应处理状态: True");
                }
            }
        }

        // 添加过滤<think>内容的辅助方法
        private string FilterThinkContent(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
                
            // 检查是否包含<think>标签
            if (input.Contains("<think>"))
            {
                Debug.Log($"LLMLogic检测到<think>标签，原内容长度: {input.Length}");
                
                // 循环处理所有<think>标签
                string result = input;
                bool hasMoreThinkTags = true;
                
                while (hasMoreThinkTags && !string.IsNullOrEmpty(result))
                {
                    int thinkStart = result.IndexOf("<think>");
                    
                    if (thinkStart >= 0)
                    {
                        int thinkEnd = result.IndexOf("</think>", thinkStart);
                        
                        if (thinkEnd >= 0)
                        {
                            // 移除<think>...</think>部分
                            result = result.Remove(thinkStart, thinkEnd - thinkStart + 8); // 8是</think>的长度
                        }
                        else
                        {
                            // 如果只有开始标签，移除<think>及其后面的所有内容
                            result = result.Substring(0, thinkStart);
                            hasMoreThinkTags = false; // 不需要继续处理
                        }
                    }
                    else
                    {
                        hasMoreThinkTags = false; // 没有更多<think>标签
                    }
                }
                
                Debug.Log($"LLMLogic移除所有<think>内容后长度: {result.Length}");
                return result;
            }
            
            return input;
        }

        private IEnumerator SynthesisCoroutine(string text)
        {
            if (string.IsNullOrEmpty(text))
                yield break;
            
            string conversationId = _currentConversationId ?? "unknown";
            
            // 检查是否已启用语音合成
            if (!enableVoiceSynthesis)
            {
                Debug.Log($"语音合成已禁用，仅更新文本。文本长度: {text.Length}, 对话ID: {conversationId}");
                
                // 即使不合成语音，也需要将文本添加到队列以确保更新对话框
                ResultData resultData = new ResultData { clip = null, result = text };
                
                // 添加标记，确保这是GPT响应
                gptVoice.Enqueue(resultData);
                
                // 如果还没有启动字幕同步协程，则启动它
                if (_titleSynchronization_Cor == null)
                {
                    Debug.Log($"启动字幕同步协程（无语音模式）, 对话ID: {conversationId}");
                    _titleSynchronization_Cor = _mono.StartCoroutine(TitleSynchronizationCoroutine());
                }
                
                yield break;
            }
            
            // 以下是启用语音合成的处理逻辑
            
            // 检查是否有相同内容的语音正在队列中
            bool isDuplicate = false;
            foreach (var item in gptVoice)
            {
                if (item != null && item.result == text)
                {
                    isDuplicate = true;
                    Debug.Log($"跳过重复的语音合成请求: {(text.Length > 10 ? text.Substring(0, 10) + "..." : text)}, 对话ID: {conversationId}");
                    break;
                }
            }
            
            if (isDuplicate)
                yield break;
                
            Debug.Log($"开始语音合成，文本长度: {text.Length}, 对话ID: {conversationId}");
            
            IEnumerator youdaoIE = null;
            try
            {
                youdaoIE = VoiceTTS.Synthesis(text);
            }
            catch (Exception ex)
            {
                Debug.LogError($"语音合成初始化出错: {ex.Message}");
                yield break;
            }
            
            if (youdaoIE == null)
            {
                Debug.LogError("语音合成返回空迭代器");
                yield break;
            }

            gptVoice.Enqueue(new ResultData { clip = youdaoIE, result = text });
             
            yield return youdaoIE;
            
            // 如果还没有启动字幕同步协程，则启动它
            if (_titleSynchronization_Cor == null)
            {
                Debug.Log($"启动字幕同步协程, 对话ID: {conversationId}");
                _titleSynchronization_Cor = _mono.StartCoroutine(TitleSynchronizationCoroutine());
            }
        }

        /// <summary>
        /// 字幕同步协程
        /// </summary>
        /// <returns></returns>
        private IEnumerator TitleSynchronizationCoroutine()
        {
            string conversationId = _currentConversationId ?? "unknown";
            Debug.Log($"开始字幕同步协程, 对话ID: {conversationId}");
            
            // 设置标志位，表示正在处理GPT响应
            bool flagWasSet = false;
            try
            {
                Debug.Log($"TitleSynchronizationCoroutine: 准备设置GPT响应处理状态: True, 对话ID: {conversationId}");
                LKZ.Voice.VoiceRecognizerModel.SetProcessingGPTResponse(true);
                flagWasSet = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"TitleSynchronizationCoroutine: 设置GPT响应处理状态时出错: {ex.Message}\n{ex.StackTrace}");
            }
            
            SendCommand.Send(new ChatGPTStartTalkCommand());//开始播放

            // 用于在应用程序中记住当前显示的完整文本
            string currentFullText = string.Empty;
            
            // 使用集合记录已处理的文本片段
            HashSet<string> processedTextSegments = new HashSet<string>();
            
            // 使用布尔标志位来跟踪处理状态，避免在try-catch中使用yield
            bool shouldContinue = true;
            
            try
            {
                while (shouldContinue && !isStopCreate && (gptVoice.Count > 0 || !isRequestChatGPTContent))
            { 
                if (gptVoice.Count == 0)
                {
                    yield return null;
                    continue;
                }
                    
                    ResultData result = null;
                    bool errorOccurred = false;
                    
                    // 安全地出队
                    try
                    {
                        result = gptVoice.Dequeue();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"从语音队列出队时出错: {ex.Message}");
                        errorOccurred = true;
                    }
                    
                    // 在try-catch外部使用yield语句
                    if (errorOccurred)
                    {
                    yield return null;
                        continue;
                    }
                    
                    if (result == null || string.IsNullOrEmpty(result.result))
                    {
                        Debug.Log($"跳过空语音队列项, 对话ID: {conversationId}");
                    yield return null;
                        continue;
                    }
                    
                    // 检查是否已处理过该文本段
                    if (processedTextSegments.Contains(result.result))
                    {
                        Debug.Log($"跳过已处理过的文本段: {(result.result.Length > 10 ? result.result.Substring(0, 10) + "..." : result.result)}, 对话ID: {conversationId}");
                        
                        // 仍然需要处理音频部分
                        yield return ProcessAudioClip(result, conversationId);
                        continue;
                    }
                    
                    // 将文本段添加到已处理集合
                    processedTextSegments.Add(result.result);
                    
                    Debug.Log($"处理语音队列项, 内容长度: {result.result.Length}, 对话ID: {conversationId}");

                    // 确保使用GPT回复框更新函数
                    if (_gptTextUpdateAction == null)
                    {
                        Debug.LogWarning("字幕同步协程中GPT回复框更新函数为空，重新创建");
                        LLMChatManager.Instance.CreateOrGetDialog(Enum.InfoType.ChatGPT, null);
                        _gptTextUpdateAction = text => LLMChatManager.Instance.UpdateDialogText(Enum.InfoType.ChatGPT, text);
                        hasChatGPTContentAdded = true;
                    }
                    
                    // 处理文本更新，不使用try-catch包裹yield语句
                    bool shouldUpdateText = false;
                    string textToUpdate = "";
                    
                    try
                    {
                        // 将当前片段追加到完整文本
                        if (currentFullText == null)
                        {
                            currentFullText = string.Empty;
                        }
                        
                        // 安全地检查是否包含重复内容
                        bool containsResult = false;
                        bool checkError = false;
                        
                        try 
                        {
                            // 使用精确的方法检查是否已包含该文本
                            containsResult = !string.IsNullOrEmpty(result.result) && 
                                            !string.IsNullOrEmpty(currentFullText) && 
                                            (currentFullText.Contains(result.result) || 
                                             processedResponses.Contains(result.result));
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"检查文本包含关系出错: {ex.Message}");
                            checkError = true;
                        }
                        
                        // 如果检查出错，默认不包含，避免丢失内容
                        if (checkError)
                        {
                            containsResult = false;
                        }
                        
                        if (!containsResult) // 避免重复添加相同内容
                        {
                            currentFullText += result.result;
                            textToUpdate = currentFullText;
                            shouldUpdateText = true;
                            Debug.Log($"准备更新对话框完整文本, 当前长度: {currentFullText.Length}, 内容: {(currentFullText.Length > 30 ? currentFullText.Substring(0, 30) + "..." : currentFullText)}");
                        }
                        else
                        {
                            Debug.Log($"跳过重复内容: {(result.result.Length > 20 ? result.result.Substring(0, 20) + "..." : result.result)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"准备更新UI文本时出错: {ex.Message}");
                    }
                    
                    // 在try-catch外部更新文本和yield
                    if (shouldUpdateText)
                    {
                        // 始终使用GPT回复框更新函数，而不是_showUITextAction
                        _gptTextUpdateAction(textToUpdate);
                        
                        // 如果没有启用语音合成，添加适当延迟让用户阅读
                        if (!enableVoiceSynthesis)
                        {
                            // 根据文本长度添加更长的延迟，让用户有足够时间阅读
                            float readingDelay = Mathf.Min(3.0f, 0.5f + result.result.Length * 0.05f);
                            Debug.Log($"无语音模式，延迟 {readingDelay}秒 用于阅读");
                            yield return new WaitForSeconds(readingDelay);
                        }
                        else
                        {
                            // 有语音时，短暂延迟即可
                            yield return new WaitForSeconds(0.1f);
                        }
                    }
                    
                    // 处理音频播放，不在try-catch中使用yield
                    yield return ProcessAudioClip(result, conversationId);
                }
            }
            finally
            {
                // 重置标志位，表示GPT响应处理完成
                if (flagWasSet)
                {
                    Debug.Log($"TitleSynchronizationCoroutine: 准备重置GPT响应处理状态: False, 对话ID: {conversationId}");
                    try
                    {
                        LKZ.Voice.VoiceRecognizerModel.SetProcessingGPTResponse(false);
                        Debug.Log($"TitleSynchronizationCoroutine: 已重置GPT响应处理状态, 对话ID: {conversationId}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"TitleSynchronizationCoroutine: 重置GPT响应处理状态时出错: {ex.Message}\n{ex.StackTrace}");
                    }
                }
                else
                {
                    Debug.LogWarning($"TitleSynchronizationCoroutine: 标志位未设置，跳过重置操作, 对话ID: {conversationId}");
                }
            }

            try
            {
                Debug.Log($"字幕同步协程结束, 对话ID: {conversationId}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"字幕同步协程结束记录出错: {ex.Message}\n{ex.StackTrace}");
            }
            
            // 无论是正常结束还是异常，都确保状态被正确重置
            PlayFinish();
        }
        
        /// <summary>
        /// 处理音频片段的协程
        /// </summary>
        private IEnumerator ProcessAudioClip(ResultData result, string conversationId)
        {
            if (result == null)
                yield break;
                
            // 如果未启用语音合成或clip为null，只做简单延迟后返回
            if (!enableVoiceSynthesis || result.clip == null)
            {
                // 不需要播放语音，但为了显示效果，添加一个短暂的延迟
                yield return new WaitForSeconds(0.5f);
                yield break;
            }
            
            yield return result;
            
            AudioClip clip = null;
            
            // 安全地提取音频片段
            try
            {
                if (result.clip.Current is AudioClip audioClip)
                {
                    clip = audioClip;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"提取音频片段时出错: {ex.Message}");
                yield break;
            }
            
            if (clip == null)
                yield break;
                
            // 播放音频
            try
            {
                audioModel.Play(clip);
                Debug.Log($"开始播放语音片段，长度: {clip.length}秒, 对话ID: {conversationId}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"播放音频片段时出错: {ex.Message}");
                if (clip != null)
                {
                    GameObject.Destroy(clip);
                }
                yield break;
            }
            
            // 等待音频播放完成
            while (audioModel != null && audioModel.IsPlaying)
            {
                yield return null;
            }
            
            // 清理资源
            try
            {
                GameObject.Destroy(clip);
            }
            catch (Exception ex)
            {
                Debug.LogError($"销毁音频片段时出错: {ex.Message}");
            }
        }

        void ClearGPTVoice()
        {
            try 
            {
                while (gptVoice.Count > 0)
            {
                var item = gptVoice.Dequeue();
                    // 只有在启用语音合成且音频片段存在时才需要清理AudioClip
                    if (enableVoiceSynthesis && item != null && item.clip != null && item.clip.Current is AudioClip clip)
                    {
                    GameObject.Destroy(clip);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"清理语音队列时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 播放完成
        /// </summary>
        private void PlayFinish()
        {
            try
            {
                Debug.Log($"对话完成，开始重置状态，对话ID: {_currentConversationId}");
                
                // 重置GPT响应处理标志位
                Debug.Log("PlayFinish: 准备重置GPT响应处理状态: False");
                try
                {
                    LKZ.Voice.VoiceRecognizerModel.SetProcessingGPTResponse(false);
                    Debug.Log("PlayFinish: 已重置GPT响应处理状态");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"PlayFinish: 重置GPT响应处理状态时出错: {ex.Message}\n{ex.StackTrace}");
                }
                
            isStopCreate = true;
                isRequestChatGPTContent = false;
                onceResult = string.Empty;
                currentGPTResponseText = ""; 
                responseCounter = 0;
                lastResponseTime = 0f;
                currentProcessingVoiceText = "";
                isDisplayingVoiceText = false;
                processedResponses.Clear();
                hasChatGPTContentAdded = false;
                hasUserContentAdded = false;
                _showUITextAction = null;
                _userTextUpdateAction = null;
                _gptTextUpdateAction = null;
                _currentConversationId = null;

                // 停止所有相关协程
                if (_titleSynchronization_Cor != null)
                {
                    Debug.Log("PlayFinish: 停止字幕同步协程");
                    _mono.StopCoroutine(_titleSynchronization_Cor);
            _titleSynchronization_Cor = null;
                }
                
                if (_requestGPTSegmentationCor != null)
                {
                    Debug.Log("PlayFinish: 停止GPT请求协程");
                    _mono.StopCoroutine(_requestGPTSegmentationCor);
                    _requestGPTSegmentationCor = null;
                }
                
                // 清理语音资源
            ClearGPTVoice();

                // 启用语音识别和发送完成命令
                Debug.Log("PlayFinish: 启用语音识别");
                SendCommand.Send(new SettingVoiceRecognitionCommand { IsStartVoiceRecognition = true });
                SendCommand.Send(new GenerateFinishCommand { });

                // 停止音频播放
                audioModel.Stop();
                
                Debug.Log("对话完成，已重置所有状态");
            }
            catch (Exception ex)
            {
                Debug.LogError($"PlayFinish 出错: {ex.Message}\n{ex.StackTrace}");
                
                // 即使出错，也尝试重置GPT响应处理状态
                try
                {
                    Debug.Log("PlayFinish: 在异常处理中尝试重置GPT响应处理状态: False");
                    LKZ.Voice.VoiceRecognizerModel.SetProcessingGPTResponse(false);
                    Debug.Log("PlayFinish: 在异常处理中已重置GPT响应处理状态");
                }
                catch (Exception resetEx)
                {
                    Debug.LogError($"PlayFinish: 在异常处理中重置GPT响应处理状态时出错: {resetEx.Message}");
                }
            }
        }

        /// <summary>
        /// 设置是否启用语音合成
        /// </summary>
        /// <param name="enable">是否启用</param>
        public void SetVoiceSynthesisEnabled(bool enable)
        {
            enableVoiceSynthesis = enable;
            Debug.Log($"语音合成功能已{(enable ? "启用" : "禁用")}");
        }
        
        /// <summary>
        /// 获取语音合成是否启用
        /// </summary>
        /// <returns>语音合成是否启用</returns>
        public bool IsVoiceSynthesisEnabled()
        {
            return enableVoiceSynthesis;
        }
    }
}
