using LKZ.Chat.Commands;
using LKZ.Commands.Chat;
using LKZ.Commands.Voice;
using LKZ.DependencyInject;
using LKZ.Enum;
using LKZ.GPT;
using LKZ.Logics;
using LKZ.TypeEventSystem;
using LKZ.Voice;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LKZ.Manager
{

    public sealed class GameApp : MonoBehaviour, DIAwakeInterface, IDRegisterBindingInterface
    {
        [SerializeField, TextArea]
        private string StartContent =
@"����һ����ľ�������GPT���������
�����������������
�������������������ϵwx:LKZ4251";


        private VoiceRecognizerModel voiceRecognizer;
        private LLMLogic  llmLogic;

        [Inject]
        private ISendCommand SendCommand { get; set; }

        public void DIRegisterBinding(IRegisterBinding registerBinding)
        {
            registerBinding.Binding<MonoBehaviour>().To(this);


            voiceRecognizer = new VoiceRecognizerModel();
            registerBinding.Binding<VoiceRecognizerModel>().To(voiceRecognizer);

            llmLogic = new LLMLogic();
            registerBinding.Binding<LLMLogic>().To(llmLogic);

        }

        void DIAwakeInterface.OnAwake()
        { 
            Application.runInBackground = true;

            Input.backButtonLeavesApp = true;

            Screen.sleepTimeout = SleepTimeout.NeverSleep;


            Screen.fullScreen = false; 
        }
         

        private void Start()
        {  
            SceneDependencyInjectContextManager.Instance.InjectProperty(voiceRecognizer);
            SceneDependencyInjectContextManager.Instance.InjectProperty(llmLogic);
            voiceRecognizer.Initialized();
            llmLogic.Initialized();

            // 初始化聊天管理器
            LLMChatManager.Instance.InitWithSendCommand(SendCommand);
            
            // 使用聊天管理器创建欢迎消息对话框
            LLMChatManager.Instance.CreateOrGetDialog(InfoType.ChatGPT, _ => { }, true);
            LLMChatManager.Instance.UpdateDialogText(InfoType.ChatGPT, StartContent);
            
            SendCommand.Send(new GenerateFinishCommand { });
             
            SendCommand.Send(new SettingVoiceRecognitionCommand { IsStartVoiceRecognition = true });
        }


        private void OnDestroy()
        {
            if (voiceRecognizer != null)
            {
                voiceRecognizer.OnDestroy();
            }
        }
    }
}
