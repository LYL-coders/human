using LKZ.Chat.Commands;
using LKZ.Commands.Chat;
using LKZ.DependencyInject;
using LKZ.Enum;
using LKZ.TypeEventSystem;
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LKZ.UI
{
    public sealed class ScrollViewGPTContent : MonoBehaviour, DIStartInterface, DIAwakeInterface, IBeginDragHandler, IEndDragHandler
    {
        [Inject]
        private IRegisterCommand RegisterCommand { get; set; }

        ScrollRect _scrollRect;

        private RectTransform _scrollRect_Content;

        [SerializeField]
        private GameObject _my_Go, _gpt_go;

        [SerializeField, Tooltip("���")]
        private float interval = 15f;

        [SerializeField, Tooltip("���������Ƿ������ƶ�����")]
        private float GPTGenerateContentUpMove = 30f;

        private ShowContent currentShowContent;

        /// <summary>
        /// �������ݺͲ��������ݹ�����ͼ��λ��
        /// </summary>
        private Vector3 defaultPos, GPTGenerateContentPos;
         

        private bool isSetScrollRectNormalizedPosition;

        /// <summary>
        /// �Ƿ�������GPT����
        /// </summary>
        private bool isGenerateGPTContent;

        // 用于记录上一次添加的内容类型
        private InfoType lastAddedContentType = InfoType.None;
        // 用于防止短时间内重复添加相同类型的内容
        private float lastAddContentTime = 0f;
        private const float MIN_ADD_CONTENT_INTERVAL = 0.5f; // 最小添加间隔(秒)

        private RectTransform thisRect;

        void DIAwakeInterface.OnAwake()
        {
            thisRect = base.transform as RectTransform;

            _scrollRect = GetComponent<ScrollRect>();
            _scrollRect_Content = _scrollRect.content;

            defaultPos = thisRect.anchoredPosition;
            GPTGenerateContentPos = defaultPos;
            GPTGenerateContentPos.y += GPTGenerateContentUpMove;
             
            isSetScrollRectNormalizedPosition = true;
        }

        void DIStartInterface.OnStart()
        {
            RegisterCommand.Register<AddChatContentCommand>(AddChatContentCommandCallback);

            RegisterCommand.Register<GenerateFinishCommand>(GenerateFinishCommandCallback); 

        }
         

        private void GenerateFinishCommandCallback(GenerateFinishCommand obj)
        {
            isGenerateGPTContent = false;
        }

        void IEndDragHandler.OnEndDrag(PointerEventData eventData)
        {
            isSetScrollRectNormalizedPosition = true;

        }

        void IBeginDragHandler.OnBeginDrag(PointerEventData eventData)
        {
            isSetScrollRectNormalizedPosition = false;

        }

        private void OnDestroy()
        {
            if (RegisterCommand != null)
            {
                RegisterCommand.UnRegister<AddChatContentCommand>(AddChatContentCommandCallback);
            }
        }

        private void AddChatContentCommandCallback(AddChatContentCommand obj)
        {
            // 只处理系统欢迎消息或没有特定处理者的一般消息
            
            // 检查是否是相同类型的重复添加
            if (obj.infoType == lastAddedContentType && (Time.time - lastAddContentTime < MIN_ADD_CONTENT_INTERVAL) && !obj.isSystemWelcome)
            {
                Debug.Log($"跳过重复添加的聊天内容框: {obj.infoType}, 间隔: {Time.time - lastAddContentTime}秒");
                obj._addTextAction(AddShowText); // 仍然提供文本添加功能
                return;
            }
            
            // 更新上次添加的类型和时间
            lastAddedContentType = obj.infoType;
            lastAddContentTime = Time.time;
            
            Debug.Log($"添加聊天内容框: {obj.infoType}" + (obj.isSystemWelcome ? " (系统欢迎消息)" : ""));
            
            Vector2 pos = new Vector2(0, -_scrollRect_Content.sizeDelta.y);

            if (!object.ReferenceEquals(null, currentShowContent))
                currentShowContent.Enabled = false;

            switch (obj.infoType)
            {
                case InfoType.My:
                    currentShowContent = Instantiate(_my_Go, _scrollRect_Content).GetComponent<ShowContent>();
                  //  pos.x = ScreenWidth;
                    break;
                case InfoType.ChatGPT:
                    currentShowContent = Instantiate(_gpt_go, _scrollRect_Content).GetComponent<ShowContent>();
                    isGenerateGPTContent = true;
                    break;
            }

            currentShowContent.Initialized(pos);

            lastHeight = 0;

            _scrollRect_Content.sizeDelta += new Vector2(0, interval);

            obj._addTextAction(AddShowText);
        }

        float lastHeight = default;

        private void AddShowText(string c)
        {
            currentShowContent.AddText(c);
        }

        private void LateUpdate()
        {
            if (object.ReferenceEquals(null, currentShowContent))
                return;

            if (currentShowContent.Height != lastHeight)
            {
                _scrollRect_Content.sizeDelta += new Vector2(0, currentShowContent.Height - lastHeight);

                lastHeight = currentShowContent.Height;
            }

            if (isSetScrollRectNormalizedPosition)
                _scrollRect.verticalNormalizedPosition = Mathf.Lerp(_scrollRect.verticalNormalizedPosition, 0, 0.05f);

#if !UNITY_STANDALONE_WIN
            //���������ƶ�
            thisRect.anchoredPosition = Vector3.Lerp(this.thisRect.anchoredPosition, isGenerateGPTContent ? this.GPTGenerateContentPos : this.defaultPos, 0.05f);
#endif
        }


    }
}