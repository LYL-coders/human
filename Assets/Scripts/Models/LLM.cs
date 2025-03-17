using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace LKZ.Models
{
    [Serializable]
    public class ChatGPTResponse
    {
        public Choice[] choices;
        
        [Serializable]
        public class Choice
        {
            public Message message;
        }
        
        [Serializable]
        public class Message
        {
            public string content;
        }
    }

    [Serializable]
    public class ChatGPTStreamResponse
    {
        public Choice[] choices;
        
        [Serializable]
        public class Choice
        {
            public Delta delta;
        }
        
        [Serializable]
        public class Delta
        {
            public string content;
        }
    }

    public class ModelLLM : MonoBehaviour
    {
        public IEnumerator RequestGPTSegmentation(string message, Action<string, bool> callback)
        {
            // 在try块外部声明所有需要的变量
            bool processingComplete = false;
            bool finalCallbackSent = false;
            ChatGPTResponse jsonResponse = null;
            string filteredContent = "";
            List<string> segments = new List<string>();
            bool parseSuccess = false;
            bool flagWasSet = false;
            
            try
            {
                // 设置标志位，表示正在处理GPT响应
                Debug.Log("Models/LLM.cs: 准备设置GPT响应处理状态: True");
                LKZ.Voice.VoiceRecognizerModel.SetProcessingGPTResponse(true);
                flagWasSet = true;
                
                Debug.Log($"开始处理GPT请求: {(message.Length > 20 ? message.Substring(0, 20) + "..." : message)}");
                
                // 如果是完整的JSON响应，模拟流式输出
                if (message.StartsWith("{") && message.EndsWith("}"))
                {
                    Debug.Log("检测到完整JSON响应，模拟流式输出");
                    
                    try
                    {
                        // 解析JSON响应
                        jsonResponse = JsonUtility.FromJson<ChatGPTResponse>(message);
                        
                        if (jsonResponse != null && !string.IsNullOrEmpty(jsonResponse.choices[0].message.content))
                        {
                            string content = jsonResponse.choices[0].message.content;
                            
                            // 过滤掉<think>内容
                            filteredContent = FilterThinkContent(content);
                            
                            if (string.IsNullOrEmpty(filteredContent))
                            {
                                Debug.LogWarning("过滤后的内容为空，跳过模拟流式输出");
                                callback?.Invoke("", true);
                                finalCallbackSent = true;
                            }
                            else
                            {
                                // 按标点符号分段
                                segments = SplitByPunctuation(filteredContent);
                                parseSuccess = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"解析JSON响应出错: {ex.Message}");
                        // 继续处理，尝试作为普通文本处理
                    }
                }
                else
                {
                    // 处理普通文本响应
                    string[] lines = message.Split('\n');
                    
                    foreach (string line in lines)
                    {
                        if (string.IsNullOrEmpty(line))
                        {
                            continue;
                        }
                        
                        if (line.StartsWith("data: "))
                        {
                            string data = line.Substring(6);
                            
                            if (data == "[DONE]")
                            {
                                Debug.Log("收到流式响应结束标记 [DONE]");
                                callback?.Invoke("", true);
                                finalCallbackSent = true;
                                break;
                            }
                            
                            try
                            {
                                var response = JsonUtility.FromJson<ChatGPTStreamResponse>(data);
                                
                                if (response != null && response.choices.Length > 0)
                                {
                                    string content = response.choices[0].delta.content;
                                    
                                    if (!string.IsNullOrEmpty(content))
                                    {
                                        Debug.Log($"处理流式响应片段: {(content.Length > 10 ? content.Substring(0, 10) + "..." : content)}");
                                        callback?.Invoke(content, false);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"解析流式响应出错: {ex.Message}, 数据: {data}");
                            }
                        }
                    }
                    
                    // 确保最后一次回调被调用
                    if (!finalCallbackSent)
                    {
                        callback?.Invoke("", true);
                        finalCallbackSent = true;
                    }
                }
                
                processingComplete = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"RequestGPTSegmentation 出错: {ex.Message}\n{ex.StackTrace}");
                if (!finalCallbackSent)
                {
                    callback?.Invoke("", true);
                    finalCallbackSent = true;
                }
                processingComplete = true;
            }
            finally
            {
                // 如果不是处理JSON响应的情况，直接完成
                if (!parseSuccess || segments.Count == 0)
                {
                    // 重置标志位，表示GPT响应处理完成
                    if (flagWasSet)
                    {
                        Debug.Log("Models/LLM.cs: 准备重置GPT响应处理状态: False (在finally块中)");
                        try
                        {
                            LKZ.Voice.VoiceRecognizerModel.SetProcessingGPTResponse(false);
                            Debug.Log("Models/LLM.cs: 已重置GPT响应处理状态 (在finally块中)");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Models/LLM.cs: 重置GPT响应处理状态时出错: {ex.Message}\n{ex.StackTrace}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning("Models/LLM.cs: 标志位未设置，跳过重置操作 (在finally块中)");
                    }
                    processingComplete = true;
                }
            }
            
            // 在try-catch块外处理yield语句
            if (parseSuccess && segments.Count > 0)
            {
                // 模拟流式输出
                for (int i = 0; i < segments.Count; i++)
                {
                    string segment = segments[i];
                    
                    if (string.IsNullOrEmpty(segment))
                    {
                        continue;
                    }
                    
                    // 调用回调函数，传递当前段落和是否是最后一个段落
                    bool isLast = (i == segments.Count - 1);
                    Debug.Log($"模拟流式输出段落 {i+1}/{segments.Count}: {(segment.Length > 20 ? segment.Substring(0, 20) + "..." : segment)}, 是否最后: {isLast}");
                    
                    callback?.Invoke(segment, isLast);
                    
                    // 增加延迟，让系统有时间处理每个段落
                    yield return new WaitForSeconds(0.2f);
                }
                
                // 重置标志位，表示GPT响应处理完成
                if (flagWasSet)
                {
                    Debug.Log("Models/LLM.cs: 准备重置GPT响应处理状态: False (在流式输出后)");
                    try
                    {
                        LKZ.Voice.VoiceRecognizerModel.SetProcessingGPTResponse(false);
                        Debug.Log("Models/LLM.cs: 已重置GPT响应处理状态 (在流式输出后)");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Models/LLM.cs: 重置GPT响应处理状态时出错: {ex.Message}\n{ex.StackTrace}");
                    }
                }
                else
                {
                    Debug.LogWarning("Models/LLM.cs: 标志位未设置，跳过重置操作 (在流式输出后)");
                }
                yield break;
            }
            
            // 等待处理完成
            while (!processingComplete)
            {
                yield return null;
            }
            
            yield break;
        }
        
        // 辅助方法：按标点符号分段
        private List<string> SplitByPunctuation(string text)
        {
            List<string> segments = new List<string>();
            if (string.IsNullOrEmpty(text))
                return segments;
                
            char[] punctuations = new char[] { '。', '！', '？', '；', '.', '!', '?', ';' };
            int lastIndex = 0;
            
            for (int i = 0; i < text.Length; i++)
            {
                if (Array.IndexOf(punctuations, text[i]) >= 0 || i == text.Length - 1)
                {
                    if (i >= lastIndex)
                    {
                        int length = i - lastIndex + 1;
                        if (length > 0 && lastIndex + length <= text.Length)
                        {
                            string segment = text.Substring(lastIndex, length);
                            if (!string.IsNullOrEmpty(segment))
                            {
                                segments.Add(segment);
                                lastIndex = i + 1;
                            }
                        }
                    }
                }
            }
            
            // 如果没有找到分段点，将整个文本作为一段
            if (segments.Count == 0 && !string.IsNullOrEmpty(text))
            {
                segments.Add(text);
            }
            
            return segments;
        }
        
        // 辅助方法：过滤<think>内容
        private string FilterThinkContent(string input)
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
                
                // 如果结果为空但原内容不为空，返回空字符串
                if (string.IsNullOrWhiteSpace(result) && !string.IsNullOrEmpty(input))
                {
                    return "";
                }
                
                return result;
            }
            
            return input;
        }
    }
} 