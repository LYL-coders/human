using System;
using System.Text;
using UnityEngine.Networking;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using LKZ.Logics;
using Newtonsoft.Json.Linq;

namespace LKZ.GPT
{
    public sealed class Certificate : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData)
        {
            return true;
        }
    }
    public static class LLM
    {
        private readonly static WaitForSeconds wait_internal = new WaitForSeconds(0.2f);

        private readonly static char[] Segmentations = new char[] { '；', ';', '。', ':', '：', '！', '!', '?', '？', ',', '，' };

        [Serializable]
        public class UserDataStruct
        {
            public string model;
            public List<Message> messages;
            public bool stream = true;
            public float temperature = 0.7f;
            public float top_p = 0.95f;
            public int max_tokens = 2000;
            public float presence_penalty = 0f;
            public float frequency_penalty = 0f;
        }

        [Serializable]
        public class Message
        {
            private Message() { }

            public string role;
            public string content;

            public static Message CreateSystemMessage(string content)
            {
                return new Message() { role = "system", content = content };
            }

            public static Message CreateUserMessage(string content)
            {
                return new Message() { role = "user", content = content };
            }

            public static Message CreateAssistantMessage(string content)
            {
                return new Message() { role = "assistant", content = content };
            }
        }

        readonly static UserDataStruct userDataStruct;
        static readonly LLMConfig config;
        static LLM()
        {
            config = UnityEngine.Resources.Load<LLMConfig>("LLMConfig");

            userDataStruct = new UserDataStruct()
            {
                model = config.model,
                stream = true, // 始终使用流式输出模式
                messages = new List<Message>(),
                temperature = 0.7f,
                top_p = 0.95f,
                max_tokens = 2000
            };
            userDataStruct.messages.Add(Message.CreateSystemMessage(config.roleSetting));
        }

        /// <summary>
        /// 向大模型发送请求
        /// </summary>
        /// <param name="content">用户输入内容</param>
        /// <param name="callback">回调函数，处理流式响应</param>
        /// <returns>协程</returns>
        public static IEnumerator Request(string content, Action<string, bool> callback)
        {
            // 清除之前的用户消息，只保留系统角色设定
            userDataStruct.messages.Clear();
            userDataStruct.messages.Add(Message.CreateSystemMessage(config.roleSetting));
            
            // 添加新的用户消息
            userDataStruct.messages.Add(Message.CreateUserMessage(content));
            
            // 确保始终使用流式响应模式
            userDataStruct.stream = true;

            var jsonData = JsonUtility.ToJson(userDataStruct);
            Debug.Log($"Sending request with data: {jsonData}");

            // 确保 URL 以 /v1/chat/completions 结尾
            string url = config.url;
            if (!url.EndsWith("/v1/chat/completions"))
            {
                if (url.EndsWith("/v1"))
                {
                    url = url.TrimEnd("/v1".ToCharArray()) + "/v1/chat/completions";
                }
                else if (!url.EndsWith("/"))
                {
                    url += "/v1/chat/completions";
                }
                else
                {
                    url += "v1/chat/completions";
                }
                Debug.Log($"Adjusted URL to: {url}");
            }

            return RequestGPTSegmentation(jsonData, callback, url);
        }


        private static IEnumerator RequestGPTSegmentation(string requestData, Action<string, bool> callback, string url)
        {
            bool flagWasSet = false;
            UnityWebRequest request = null;
            UnityWebRequestAsyncOperation asyncOp = null;
            string last = "";
            string mess = "";
            bool callbackInvoked = false; // 保留标志位，但只用于跟踪回调函数是否已被调用
            
            try
            {
                // 设置标志位，表示正在处理GPT响应
                Debug.Log("准备设置GPT响应处理状态: True");
                LKZ.Voice.VoiceRecognizerModel.SetProcessingGPTResponse(true);
                flagWasSet = true;
                
                Debug.Log($"当前使用的模型: {userDataStruct.model}");

                request = new UnityWebRequest(url, "POST");
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(requestData))
                { contentType = "application/json" };

                Debug.Log($"Request URL: {url}");
                
                // 安全地记录 API key
                string maskedKey = "***";
                try
                {
                    if (!string.IsNullOrEmpty(config?.key) && config.key.Length > 5)
                    {
                        maskedKey = config.key[..5] + "...";
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error masking API key: {ex.Message}");
                }
                Debug.Log($"Request Headers: Authorization: Bearer {maskedKey}");

                request.downloadHandler = new DownloadHandlerBuffer();
                request.certificateHandler = new Certificate();

                // 设置所有必要的请求头
                if (string.IsNullOrEmpty(config?.key) && !userDataStruct.model.Equals("fay", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogError("API key is not set!");
                    if (callback != null)
                    {
                        callback.Invoke("API密钥未设置", true);
                    }
                    yield break;
                }

                // 只有在非 fay 模型时才设置 Authorization 头
                if (!userDataStruct.model.Equals("fay", StringComparison.OrdinalIgnoreCase))
                {
                    request.SetRequestHeader("Authorization", $"Bearer {config.key}");
                }
                else
                {
                    Debug.Log("使用fay模型，不设置Authorization头");
                }
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "text/event-stream");
                request.SetRequestHeader("Connection", "keep-alive");
                request.SetRequestHeader("Cache-Control", "no-cache");
                request.SetRequestHeader("User-Agent", "Unity/1.0");

                Debug.Log($"Full request data: {requestData}");
                Debug.Log($"请求URL: {url}, 模型: {userDataStruct.model}, 是否流式输出: {userDataStruct.stream}");

                asyncOp = request.SendWebRequest();
                
                // 等待请求完成
                Debug.Log("等待请求完成...");
            }
            catch (Exception ex)
            {
                Debug.LogError($"RequestGPTSegmentation 初始化请求时出错: {ex.Message}\n{ex.StackTrace}");
                if (callback != null && !callbackInvoked)
                {
                    callbackInvoked = true;
                    callback.Invoke("初始化请求时出错", true);
                }
                
                if (request != null)
                {
                    request.Dispose();
                }
                
                // 重置标志位
                if (flagWasSet)
                {
                    try
                    {
                        Debug.Log("准备重置GPT响应处理状态: False (初始化错误)");
                        LKZ.Voice.VoiceRecognizerModel.SetProcessingGPTResponse(false);
                        Debug.Log("已重置GPT响应处理状态 (初始化错误)");
                    }
                    catch (Exception resetEx)
                    {
                        Debug.LogError($"重置GPT响应处理状态时出错: {resetEx.Message}");
                    }
                }
                
                yield break;
            }
            
            // 在try-catch块外等待请求完成
            yield return asyncOp;
            
            try
            {
                // 检查请求是否成功
                if (request.result == UnityWebRequest.Result.ConnectionError || 
                    request.result == UnityWebRequest.Result.ProtocolError ||
                    request.result == UnityWebRequest.Result.DataProcessingError)
                {
                    Debug.LogError($"请求错误: {request.error}, 响应码: {request.responseCode}");
                    if (request.downloadHandler != null && !string.IsNullOrEmpty(request.downloadHandler.text))
                    {
                        Debug.LogError($"错误响应内容: {request.downloadHandler.text}");
                    }
                    if (callback != null && !callbackInvoked)
                    {
                        callbackInvoked = true;
                        callback.Invoke($"请求错误: {request.error}", true);
                    }
                }
                else
                {
                    string responseText = request.downloadHandler?.text ?? "";
                    Debug.Log($"请求成功，响应长度: {responseText.Length}, 响应码: {request.responseCode}");
                    if (responseText.Length > 0)
                    {
                        Debug.Log($"响应前100个字符: {(responseText.Length > 100 ? responseText.Substring(0, 100) + "..." : responseText)}");
                    }
                    else
                    {
                        Debug.LogWarning("响应内容为空");
                    }
                    
                    // 处理响应
                    ProcessResponse(responseText, true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"RequestGPTSegmentation 处理响应时出错: {ex.Message}\n{ex.StackTrace}");
                if (callback != null && !callbackInvoked)
                {
                    callbackInvoked = true;
                    callback.Invoke("处理响应时出错", true);
                }
            }
            finally
            {
                // 清理资源
                if (request != null)
                {
                    request.Dispose();
                }
                
                // 重置标志位，表示GPT响应处理完成
                if (flagWasSet)
                {
                    try
                    {
                        Debug.Log("准备重置GPT响应处理状态: False");
                        LKZ.Voice.VoiceRecognizerModel.SetProcessingGPTResponse(false);
                        Debug.Log("已重置GPT响应处理状态");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"重置GPT响应处理状态时出错: {ex.Message}\n{ex.StackTrace}");
                    }
                }
                else
                {
                    Debug.LogWarning("标志位未设置，跳过重置操作");
                }
            }
            
            // 处理响应的方法
            void ProcessResponse(string str, bool isComplete)
            {
                if (callbackInvoked)
                {
                    Debug.Log("回调已调用，跳过响应处理");
                    return;
                }

                if (string.IsNullOrEmpty(str))
                {
                    Debug.LogWarning("Response text is empty");
                    // 对于fay模型，即使响应为空也尝试发送默认回复
                    if (userDataStruct.model.Equals("fay", StringComparison.OrdinalIgnoreCase) && !callbackInvoked)
                    {
                        Debug.Log("fay模型响应为空，发送默认回复");
                        callbackInvoked = true;
                        callback?.Invoke("你好！有什么我可以帮助你的吗？", true);
                    }
                    return;
                }

                Debug.Log($"处理响应，长度: {str.Length}, 模型: {userDataStruct.model}");

                // 对于fay模型，可能需要特殊处理
                if (userDataStruct.model.Equals("fay", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log($"处理fay模型响应: {(str.Length > 50 ? str.Substring(0, 50) + "..." : str)}");
                    
                    // 尝试直接解析为JSON
                    try
                    {
                        var jsonP = JToken.Parse(str);
                        Debug.Log("成功解析fay模型响应为JSON");
                        
                        // 尝试提取消息内容
                        string content = null;
                        
                        // 尝试不同的JSON路径
                        content = jsonP["choices"]?[0]?["message"]?["content"]?.ToString()
                            ?? jsonP["choices"]?[0]?["text"]?.ToString()
                            ?? jsonP["choices"]?[0]?["delta"]?["content"]?.ToString()
                            ?? jsonP["message"]?["content"]?.ToString()
                            ?? jsonP["content"]?.ToString()
                            ?? jsonP["text"]?.ToString();
                        
                        if (!string.IsNullOrEmpty(content))
                        {
                            Debug.Log($"从fay模型响应中提取内容: {(content.Length > 50 ? content.Substring(0, 50) + "..." : content)}");
                            content = FilterThinkContent(content);
                            
                            if (!string.IsNullOrEmpty(content))
                            {
                                Debug.Log("调用回调函数，传递fay模型响应内容");
                                callbackInvoked = true;
                                callback?.Invoke(content, true);
                                return;
                            }
                        }
                        else
                        {
                            Debug.LogWarning("无法从fay模型响应中提取内容");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"解析fay模型响应出错: {ex.Message}");
                        // 继续尝试其他处理方式
                    }
                    
                    // 如果JSON解析失败，尝试直接使用响应文本
                    if (!string.IsNullOrEmpty(str))
                    {
                        string filteredStr = FilterThinkContent(str);
                        if (!string.IsNullOrEmpty(filteredStr))
                        {
                            Debug.Log($"使用过滤后的fay模型响应文本: {(filteredStr.Length > 50 ? filteredStr.Substring(0, 50) + "..." : filteredStr)}");
                            callbackInvoked = true;
                            callback?.Invoke(filteredStr, true);
                            return;
                        }
                    }
                    
                    // 如果所有尝试都失败，使用默认回复
                    Debug.Log("无法处理fay模型响应，使用默认回复");
                    callbackInvoked = true;
                    callback?.Invoke("你好！有什么我可以帮助你的吗？", true);
                    return;
                }

                string temp = "";
                if (!string.IsNullOrEmpty(last))
                {
                    temp = str.Replace(last, "");
                }
                else
                {
                    temp = str;
                }

                last = str;

                // 检查是否是错误响应
                if (str.Contains("error"))
                {
                    try
                    {
                        var errorJson = JToken.Parse(str);
                        var errorMessage = errorJson["error"]?.ToString() ?? "Unknown error";
                        Debug.LogError($"API Error: {errorMessage}");
                        if (!callbackInvoked && callback != null)
                        {
                            callbackInvoked = true;
                            callback.Invoke($"API错误: {errorMessage}", true);
                        }
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error parsing error response: {ex.Message}");
                    }
                }

                // 检查是否是完整的JSON响应(非流式)
                if (str.Trim().StartsWith("{") && str.Trim().EndsWith("}") && !str.Contains("data:"))
                {
                    try
                    {
                        Debug.Log("检测到完整JSON响应，转为模拟流式响应");
                        
                        // 确保标志位设置为true，表示正在处理GPT响应
                        LKZ.Voice.VoiceRecognizerModel.SetProcessingGPTResponse(true);
                        
                        var jsonP = JToken.Parse(str);
                        var messageContent = jsonP["choices"]?[0]?["message"]?["content"]?.ToString();
                        
                        if (!string.IsNullOrEmpty(messageContent))
                        {
                            // 过滤掉<think>内容
                            messageContent = FilterThinkContent(messageContent);
                            
                            // 如果过滤后内容为空，直接返回，让后续逻辑处理
                            if (string.IsNullOrEmpty(messageContent))
                            {
                                Debug.Log("过滤后内容为空，跳过分段处理");
                                return;
                            }
                            
                            // 将完整响应分段处理，模拟流式输出
                            Debug.Log($"从完整响应中提取并过滤后的消息长度: {messageContent.Length}");
                            
                            // 按标点符号切分文本为多个部分
                            List<string> segments = new List<string>();
                            int lastIndex = 0;
                            
                            for (int i = 0; i < messageContent.Length; i++)
                            {
                                if (Array.IndexOf(Segmentations, messageContent[i]) >= 0 || i == messageContent.Length - 1)
                                {
                                    if (i >= lastIndex) // 确保有内容可以提取
                                    {
                                        int length = i - lastIndex + 1;
                                        if (length > 0 && lastIndex + length <= messageContent.Length)
                                        {
                                            string segment = messageContent.Substring(lastIndex, length);
                                            if (!string.IsNullOrEmpty(segment))
                                            {
                                                segments.Add(segment);
                                                lastIndex = i + 1;
                                            }
                                        }
                                    }
                                }
                                
                                // 为了防止段落过长，检查从上次分段点到现在的长度
                                if (i - lastIndex > 30 && lastIndex < messageContent.Length)
                                {
                                    int length = Math.Min(30, messageContent.Length - lastIndex);
                                    if (length > 0)
                                    {
                                        string segment = messageContent.Substring(lastIndex, length);
                                        if (!string.IsNullOrEmpty(segment))
                                        {
                                            segments.Add(segment);
                                            lastIndex += length;
                                        }
                                    }
                                }
                            }
                            
                            // 如果没有找到分段，就将整个文本作为一段
                            if (segments.Count == 0 && !string.IsNullOrEmpty(messageContent))
                            {
                                segments.Add(messageContent);
                            }
                            
                            // 为每个分段调用一次回调，模拟流式输出
                            for (int i = 0; i < segments.Count; i++)
                            {
                                // 只有最后一段标记为完成
                                bool isLastSegment = (i == segments.Count - 1);
                                
                                if (!callbackInvoked && callback != null)
                                {
                                    string segment = segments[i];
                                    Debug.Log($"模拟流式输出，分段 {i+1}/{segments.Count}: {(segment.Length > 10 ? segment.Substring(0, 10) + "..." : segment)}");
                                    
                                    // 确保回调不会被重复调用
                                    if (isLastSegment)
                                    {
                                        callbackInvoked = true;
                                    }
                                    
                                    // 调用回调函数，传递分段内容和是否是最后一段
                                    callback.Invoke(segment, isLastSegment);
                                    
                                    // 给系统一些时间处理当前分段
                                    System.Threading.Thread.Sleep(200);
                                }
                            }
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"解析完整JSON响应出错: {ex.Message}");
                    }
                }

                // 处理标准流式响应
                var datas = temp.Split("data:");

                foreach (var requestJson in datas)
                { 
                    if (string.IsNullOrEmpty(requestJson))
                        continue;

                    if (requestJson.Contains("[DONE]"))
                        break;

                    try
                    {
                        var jsonP = JToken.Parse(requestJson.Replace("data:", "").Trim());
                        
                        if (jsonP == null || !jsonP.HasValues)
                        {
                            Debug.LogWarning("Invalid JSON response");
                            continue;
                        }

                        var choices = jsonP["choices"];
                        
                        if (choices == null || !choices.HasValues || !choices[0].HasValues)
                        {
                            // 尝试其他格式
                            var content = jsonP["content"]?.ToString();
                            if (!string.IsNullOrEmpty(content))
                            {
                                content = FilterThinkContent(content);
                                if (!string.IsNullOrEmpty(content))
                                    mess += content;
                                continue;
                            }

                            var message = jsonP["message"]?.ToString();
                            if (!string.IsNullOrEmpty(message))
                            {
                                message = FilterThinkContent(message);
                                if (!string.IsNullOrEmpty(message))
                                    mess += message;
                                continue;
                            }

                            Debug.LogWarning($"No choices or alternative content in response.");
                            continue;
                        }

                        var item = choices[0];
                        
                        // 检查 message.content 格式
                        var messageContent = item["message"]?["content"]?.ToString();
                        if (!string.IsNullOrEmpty(messageContent))
                        {
                            messageContent = FilterThinkContent(messageContent);
                            Debug.Log($"Found message.content (filtered): {messageContent}");
                            if (!string.IsNullOrEmpty(messageContent))
                                mess += messageContent;
                            continue;
                        }
                        
                        // 尝试其他格式
                        var delta = item["delta"] ?? item["text"] ?? item["content"];
                        if (delta == null)
                        {
                            Debug.LogWarning("No delta/text/content in response");
                            continue;
                        }

                        var tt = delta.Type == JTokenType.String ? 
                            delta.ToString() : 
                            delta.SelectToken("content")?.ToString();

                        if (!string.IsNullOrEmpty(tt))
                        {
                            tt = tt.Trim();
                            tt = FilterThinkContent(tt);
                            if (!string.IsNullOrEmpty(tt))
                                mess += tt;
                        }

                        var finish = item.SelectToken("finish_reason");
                        if (finish != null && finish.ToString() == "stop")
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error parsing JSON: {ex.Message}\nJSON: {requestJson}");
                        continue;
                    }
                } 

                // 处理累积的消息
                string text2 = "";
                if (!isComplete)
                {
                    // 如果mess不为空，尝试按标点符号分段
                    if (!string.IsNullOrEmpty(mess))
                    {
                        // 确保mess中不包含<think>内容
                        mess = FilterThinkContent(mess);
                        
                        if (!string.IsNullOrEmpty(mess))
                        {
                            int index = -1;
                            foreach (var item in Segmentations)
                            {
                                if (mess.Contains(item))
                                {
                                    index = mess.IndexOf(item);
                                    break;
                                }
                            }

                            if (index >= 0)
                            {
                                ++index;
                                text2 = mess.Substring(0, index);
                                mess = mess.Remove(0, index);
                                Debug.Log($"找到分段点，提取内容: {(text2.Length > 20 ? text2.Substring(0, 20) + "..." : text2)}");
                            }
                            // 如果没有找到标点符号但文本足够长，也进行分段
                            else if (mess.Length > 30)
                            {
                                text2 = mess.Substring(0, 30);
                                mess = mess.Remove(0, 30);
                                Debug.Log($"文本较长，强制分段: {text2}");
                            }
                        }
                    }
                }
                else
                {
                    // 如果是最后一次处理，输出所有剩余内容
                    text2 = FilterThinkContent(mess);
                    mess = "";
                    Debug.Log($"最终消息长度: {text2?.Length ?? 0}");
                }

                if (!callbackInvoked && callback != null && !string.IsNullOrEmpty(text2))
                {
                    // 只有在完成时才标记回调已调用，这样允许流式输出多次调用回调
                    if (isComplete)
                    {
                        Debug.Log($"最终回调，内容长度: {text2.Length}, 标记回调已完成");
                        callbackInvoked = true;
                    }
                    else
                    {
                        Debug.Log($"流式回调，内容长度: {text2.Length}, 继续等待更多数据");
                    }
                    
                    // 调用回调函数，传递分段内容和是否是最后一段
                    callback.Invoke(text2, isComplete);
                }
                else if (isComplete && !callbackInvoked && callback != null)
                {
                    // 如果最后一次调用但内容为空或者过滤后为空
                    // 确保至少调用一次回调，但使用更适当的默认消息
                    Debug.Log("最终回调但没有有效内容，发送默认回应");
                    callbackInvoked = true;
                    callback.Invoke("你好！有什么我可以帮助你的吗？", true);
                }
            }
            
            yield break;
        }

        // 添加过滤<think>内容的辅助方法
        static string FilterThinkContent(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
                
            // 检查是否包含<think>标签
            if (input.Contains("<think>"))
            {
                Debug.Log($"检测到<think>标签，原内容长度: {input.Length}");
                
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
                
                Debug.Log($"移除所有<think>内容后长度: {result.Length}");
                
                // 特殊处理：如果结果为空但原内容不为空，返回一个默认消息
                // 但仅当这是最终完成响应时才使用默认消息，避免流式响应中发送多余消息
                if (string.IsNullOrWhiteSpace(result) && !string.IsNullOrEmpty(input))
                {
                    // 不自动添加默认消息，而是返回空字符串，让上层代码决定如何处理
                    return "";
                }
                
                return result;
            }
            
            return input;
        }
    }
}
