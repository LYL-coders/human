using LKZ.Chat.Commands;
using LKZ.DependencyInject;
using LKZ.Enum;
using LKZ.TypeEventSystem;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace LKZ.GPT
{
    /// <summary>
    /// 全局聊天管理器，负责协调聊天内容的创建和更新
    /// </summary>
    public sealed class LLMChatManager
    {
        // 单例实例
        private static LLMChatManager _instance;
        public static LLMChatManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new LLMChatManager();
                }
                return _instance;
            }
        }

        // 当前活跃的聊天框
        private Dictionary<InfoType, Action<string>> _activeDialogTextUpdaters = new Dictionary<InfoType, Action<string>>();
        
        // 防止重复创建标志位
        private InfoType _lastCreatedType = InfoType.None;
        private float _lastCreationTime = 0f;
        private const float MIN_CREATION_INTERVAL = 0.5f;

        // 依赖注入的SendCommand，通过InitWithSendCommand方法初始化
        private ISendCommand _sendCommand;
        
        // 标记是否已经初始化
        private bool _isInitialized = false;

        // 记录每个对话框的最后更新内容
        private Dictionary<InfoType, string> _lastDialogContents = new Dictionary<InfoType, string>();
        
        // 记录对话框的更新状态
        private Dictionary<InfoType, bool> _dialogUpdateStates = new Dictionary<InfoType, bool>();

        /// <summary>
        /// 使用SendCommand初始化管理器
        /// </summary>
        public void InitWithSendCommand(ISendCommand sendCommand)
        {
            if (_isInitialized) return;
            
            _sendCommand = sendCommand;
            _isInitialized = true;
            Debug.Log("LLMChatManager 已初始化");
        }

        /// <summary>
        /// 创建或获取对话框，并提供文本更新回调
        /// </summary>
        public void CreateOrGetDialog(InfoType type, Action<string> textUpdateCallback, bool isSystemWelcome = false)
        {
            if (!_isInitialized)
            {
                Debug.LogError("LLMChatManager 尚未初始化，请先调用 InitWithSendCommand");
                return;
            }

            try
            {
                // 防止短时间内重复创建
                if (type == _lastCreatedType && Time.time - _lastCreationTime < MIN_CREATION_INTERVAL && !isSystemWelcome)
                {
                    Debug.Log($"LLMChatManager: 跳过重复创建的对话框类型: {type}, 间隔: {Time.time - _lastCreationTime}秒");
                    return;
                }

                // 更新最后创建的类型和时间
                _lastCreatedType = type;
                _lastCreationTime = Time.time;

                // 创建新的对话框
                Debug.Log($"LLMChatManager: 创建对话框类型: {type}" + (isSystemWelcome ? " (系统欢迎)" : ""));
                
                // 重置对话框状态
                _dialogUpdateStates[type] = false;
                _lastDialogContents[type] = string.Empty;

                // 清除之前同类型的活跃对话框
                if (_activeDialogTextUpdaters.ContainsKey(type))
                {
                    _activeDialogTextUpdaters.Remove(type);
                    Debug.Log($"LLMChatManager: 移除了已存在的对话框类型: {type}");
                }

                // 添加到活跃对话框集合
                var command = new AddChatContentCommand 
                { 
                    infoType = type,
                    isSystemWelcome = isSystemWelcome,
                    _addTextAction = updater => 
                    {
                        try
                        {
                            _activeDialogTextUpdaters[type] = text =>
                            {
                                // 不在这里进行重复内容检查，统一在UpdateDialogText方法中处理
                                // 这里只负责将更新器注册到系统中
                                
                                // 调用实际的更新器
                                updater(text);
                                
                                // 标记更新状态
                                _dialogUpdateStates[type] = true;
                                
                                Debug.Log($"LLMChatManager: 成功更新对话框内容: {type}");
                            };

                            if (textUpdateCallback != null)
                            {
                                textUpdateCallback?.Invoke(string.Empty);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"LLMChatManager: 设置文本更新器时发生错误: {ex.Message}");
                        }
                    }
                };

                _sendCommand.Send(command);
                Debug.Log($"LLMChatManager: 成功发送创建对话框命令: {type}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"LLMChatManager: 创建对话框时发生错误: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 更新指定类型对话框的文本
        /// </summary>
        public void UpdateDialogText(InfoType type, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                Debug.LogWarning($"LLMChatManager: 尝试更新空文本: {type}");
                return;
            }

            if (_activeDialogTextUpdaters.TryGetValue(type, out var updater))
            {
                // 检查是否是重复内容或者是否包含在当前内容中
                if (_lastDialogContents.TryGetValue(type, out string lastContent))
                {
                    // 完全相同的内容
                    if (lastContent == text)
                    {
                        Debug.Log($"LLMChatManager: 跳过完全相同的内容更新: {type}");
                        return;
                    }
                    
                    // 新内容是旧内容的子集
                    if (text.Length < lastContent.Length && lastContent.Contains(text))
                    {
                        Debug.Log($"LLMChatManager: 跳过子集内容更新: {type}, 新内容: {text}, 已有内容长度: {lastContent.Length}");
                        return;
                    }
                    
                    // 旧内容是新内容的子集，说明是增量更新，这种情况允许
                    if (text.Length > lastContent.Length && text.Contains(lastContent))
                    {
                        Debug.Log($"LLMChatManager: 允许增量更新: {type}, 新增内容长度: {text.Length - lastContent.Length}");
                    }
                }

                updater?.Invoke(text);
                _lastDialogContents[type] = text; // 更新最后内容记录
                Debug.Log($"LLMChatManager: 更新对话框文本: {type}, 内容: {(text.Length > 20 ? text.Substring(0, 20) + "..." : text)}");
            }
            else
            {
                Debug.LogWarning($"LLMChatManager: 未找到类型为 {type} 的活跃对话框");
            }
        }

        /// <summary>
        /// 清空所有活跃对话框
        /// </summary>
        public void ClearAllDialogs()
        {
            _activeDialogTextUpdaters.Clear();
            _lastDialogContents.Clear();
            _dialogUpdateStates.Clear();
            _lastCreatedType = InfoType.None;
            Debug.Log("LLMChatManager: 已清空所有活跃对话框");
        }
    }
} 