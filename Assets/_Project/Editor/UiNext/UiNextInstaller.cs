using System;
using System.Linq;
using Gamsamo.UI;
using Gamsamo.UI.UiNext;
using Gamsamo.UI.Presentation;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Gamsamo.Editor.UiNext
{
    /// <summary>Additive UI migration. Does not invoke any scene/root generator or touch services/content.</summary>
    public static class UiNextInstaller
    {
        const string Prefab = "Assets/_Project/Prefabs/ConsultationRoomUI.prefab";
        const string Scene = "Assets/_Project/Scenes/ConsultationRoom.unity";
        const string PaperPath = "Assets/_Project/Art/UI/UiNext/PaperPanel.png";
        static TMP_FontAsset Font => AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Fonts/NotoSansKR SDF.asset");
        static readonly Color Ink = new Color32(68, 47, 36, 255);
        static readonly Color Cream = new Color32(245, 234, 211, 255);
        static Sprite Paper => AssetDatabase.LoadAllAssetsAtPath(PaperPath).OfType<Sprite>().First();
        [MenuItem("Gamsamo/UiNext/Install UI Additions")]
        public static void Install()
        {
            if (Application.isPlaying || PrefabStageUtility.GetCurrentPrefabStage() != null) throw new InvalidOperationException("Leave Play/Prefab mode first.");
            for (int i=0;i<SceneManager.sceneCount;i++) if(SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save open scenes first.");
            var importer=(TextureImporter)AssetImporter.GetAtPath(PaperPath);
            importer.textureType=TextureImporterType.Sprite; importer.spriteImportMode=SpriteImportMode.Multiple;
            #pragma warning disable 618
            importer.spritesheet=new[]{new SpriteMetaData {name="PaperPanel",rect=new Rect(24,90,1488,844),pivot=new Vector2(.5f,.5f),border=new Vector4(72,70,72,70)}};
#pragma warning restore 618
            importer.alphaIsTransparency=true;
            importer.mipmapEnabled=false; importer.maxTextureSize=2048; importer.textureCompression=TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            var root=PrefabUtility.LoadPrefabContents(Prefab);
            try { Add(root.GetComponentInChildren<ConsultationRoomController>(true)); PrefabUtility.SaveAsPrefabAsset(root,Prefab); }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            var scene=EditorSceneManager.OpenScene(Scene);
            var room=UnityEngine.Object.FindFirstObjectByType<ConsultationRoomController>(FindObjectsInactive.Include);
            Add(room); EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            Debug.Log("UiNext additions installed; existing object GUIDs and service components preserved.");
        }
        static void Add(ConsultationRoomController room)
        {
            var installed = room.GetComponentInChildren<UiNextBindings>(true);
            if(installed!=null) { Stretch((RectTransform)installed.transform); Stretch((RectTransform)installed.replayPlayer.transform); installed.GetComponent<Canvas>().overrideSorting=true; installed.GetComponent<Canvas>().sortingOrder=310; installed.replayPlayer.GetComponent<Canvas>().overrideSorting=true; installed.replayPlayer.GetComponent<Canvas>().sortingOrder=320; RefreshPaper(installed,room); FixProfileText(room.pcDesktopView.profileView); AddStatus(installed,room); return; }
            var pc=room.pcDesktopView;
            var host=new GameObject("UiNext",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));
            host.transform.SetParent(room.transform,false); Stretch((RectTransform)host.transform);
            var canvas=host.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.overrideSorting=true;canvas.sortingOrder=310;
            var scaler=host.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1920,1080);scaler.matchWidthOrHeight=.5f;
            var ports=host.AddComponent<UiFeaturePorts>(); room.uiFeaturePorts=ports;
            var bindings=host.AddComponent<UiNextBindings>();bindings.room=room;bindings.ports=ports;
            var editor=host.AddComponent<ProfileEditorView>();editor.ports=ports;editor.profile=pc.profileView;bindings.profileEditor=editor;
            editor.panel=Modal(host.transform,"ProfileEditModal");
            var card=PaperCard(editor.panel.transform,"ProfileEditPaper",410,150,1100,780);
            Label(card,"Title","프로필 편집",75,65,820,60,36);
            Label(card,"NameLabel","이름",75,155,160,35,25);
            editor.nameInput=Input(card,"NameInput","이름을 입력해 주세요",75,200,950,65,false);
            Label(card,"BioLabel","소개",75,290,160,35,25);
            editor.introductionInput=Input(card,"IntroductionInput","상담사 소개를 입력해 주세요",75,335,950,190,true);
            editor.counts=Label(card,"Counts","",75,545,950,36,21);
            editor.feedback=Label(card,"Feedback","",75,592,950,55,24);
            editor.cancelButton=Button(card,"Cancel","취소",555,675,210,60);
            editor.saveButton=Button(card,"Save","저장",795,675,230,60);
            editor.initialFocus=editor.nameInput;
            editor.panel.SetActive(false);
            MakeProfile(pc.profileView,editor);
            pc.endingsButton=Button(pc.shortcuts.transform,"UiNextEndingShortcut","후일담 다시 보기",625,585,270,48);
            var listRect=Rect(pc.transform,"UiNextEndingList",340,118,840,460);
            var list=listRect.gameObject.AddComponent<EndingListView>();pc.endingListView=list;list.ports=ports;
            var bg=listRect.gameObject.AddComponent<Image>();bg.color=Cream;
            var bar=Rect(listRect,"TitleBar",0,0,840,55).gameObject.AddComponent<Image>();bar.color=new Color32(50,100,150,255);
            Label(bar.transform,"Title","후일담 다시 보기",22,6,650,42,26).color=Color.white;
            list.closeButton=Button(listRect,"Close","닫기",720,8,100,38);
            list.playButtons=new Button[5];list.stateLabels=new TMP_Text[5];
            for(int i=0;i<5;i++)
            {
                var row=PaperCard(listRect,"Patient"+i,18,64+i*63,804,64);
                Label(row,"Name",EndingListView.PatientNames[i],30,16,105,32,25);
                list.stateLabels[i]=Label(row,"State","해금 상태 확인 전",150,19,410,30,21);
                list.playButtons[i]=Button(row,"Play","보기",620,15,150,36);
            }
            list.feedback=Label(listRect,"Feedback","",30,389,780,58,19);
            listRect.gameObject.SetActive(false);
            var replayPrefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Presentation/ClearEnding.prefab");
            var replay=(GameObject)PrefabUtility.InstantiatePrefab(replayPrefab,host.transform);
            replay.name="UiNextReplayPlayer"; Stretch((RectTransform)replay.transform); replay.GetComponent<Canvas>().overrideSorting=true; replay.GetComponent<Canvas>().sortingOrder=320;
            list.player=replay.GetComponent<EndingPresentationView>();bindings.replayPlayer=list.player;
            AddStatus(bindings,room);
        }
        static void RefreshPaper(UiNextBindings bindings, ConsultationRoomController room)
        {
            foreach(var image in bindings.GetComponentsInChildren<Image>(true))
                if(image.type==Image.Type.Sliced) image.sprite=Paper;
            foreach(var image in room.pcDesktopView.profileView.GetComponentsInChildren<Image>(true))
                if(image.type==Image.Type.Sliced) image.sprite=Paper;
            foreach(var image in room.pcDesktopView.endingListView.GetComponentsInChildren<Image>(true))
                if(image.type==Image.Type.Sliced) image.sprite=Paper;
        }
        static void FixProfileText(ProfileView profile)
        {
            foreach(var text in new[]{profile.nameText,profile.affiliationText,profile.introText}) text.textWrappingMode=TextWrappingModes.Normal;
            ScrollbarFor(profile.introText.GetComponentInParent<ScrollRect>(),365,105);
        }
        static void AddStatus(UiNextBindings bindings, ConsultationRoomController room)
        {
            if (bindings.statusDetails != null) { ScrollbarFor(bindings.statusDetails.body.GetComponentInParent<ScrollRect>(),950,430); return; }
            var view=bindings.gameObject.AddComponent<UiStatusDetailsView>();bindings.statusDetails=view;
            view.dialogue=room.GetComponentInChildren<Gamsamo.Dialogue.PatientDialogueView>(true);
            view.openButton=Button(bindings.transform,"StatusDetailsButton","상태 자세히",1500,835,250,55);
            view.panel=Modal(bindings.transform,"StatusDetailsModal");
            var card=PaperCard(view.panel.transform,"Paper",410,150,1100,780);
            Label(card,"Title","처리 상태",75,65,850,65,36);
            var scroll=Rect(card,"StatusScroll",75,170,950,430).gameObject.AddComponent<ScrollRect>();scroll.horizontal=false;
            var viewport=Rect(scroll.transform,"Viewport",0,0,950,430);viewport.gameObject.AddComponent<RectMask2D>();
            viewport.gameObject.AddComponent<Image>().color=new Color(1,1,1,.001f);
            view.body=Label(viewport,"Body","",0,0,920,430,30);view.body.overflowMode=TextOverflowModes.Overflow;
            view.body.gameObject.AddComponent<ContentSizeFitter>().verticalFit=ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport=viewport;scroll.content=view.body.rectTransform;scroll.movementType=ScrollRect.MovementType.Clamped;
            view.closeButton=Button(card,"Close","닫기",795,675,230,60);view.initialFocus=view.closeButton;
            ScrollbarFor(scroll,950,430);
            view.panel.SetActive(false);view.openButton.gameObject.SetActive(false);
        }
        static void MakeProfile(ProfileView profile,ProfileEditorView editor)
        {
            // Cover only the old right-side printed labels. Existing avatar/title bar remain intact.
            var back=Rect(profile.transform,"UiNextProfilePaper",277,66,541,380).gameObject.AddComponent<Image>();back.color=Cream;
            var paper=PaperCard(profile.transform,"UiNextProfileCard",277,62,541,386);
            Label(paper,"NameLabel","이름",30,28,110,28,19);
            profile.nameText.transform.SetParent(paper,false);Place(profile.nameText.rectTransform,140,26,365,56);profile.nameText.fontSize=22;
            Label(paper,"AffiliationLabel","소속",30,87,110,28,19);
            profile.affiliationText.transform.SetParent(paper,false);Place(profile.affiliationText.rectTransform,140,85,365,40);profile.affiliationText.fontSize=21;
            Label(paper,"IntroLabel","소개",30,134,110,28,19);
            var scroll=Rect(paper,"IntroScroll",140,130,365,105);var scrollView=scroll.gameObject.AddComponent<ScrollRect>();scrollView.horizontal=false;
            var viewport=Rect(scroll,"Viewport",0,0,365,105);viewport.gameObject.AddComponent<RectMask2D>();
            var hit=viewport.gameObject.AddComponent<Image>();hit.color=new Color(1,1,1,.001f);
            profile.introText.transform.SetParent(viewport,false);Place(profile.introText.rectTransform,0,0,350,105);
            profile.introText.fontSize=21;profile.introText.overflowMode=TextOverflowModes.Overflow;
            var fit=profile.introText.gameObject.AddComponent<ContentSizeFitter>();fit.verticalFit=ContentSizeFitter.FitMode.PreferredSize;
            scrollView.viewport=viewport;scrollView.content=profile.introText.rectTransform;scrollView.movementType=ScrollRect.MovementType.Clamped;
            profile.progressText=Label(paper,"CommittedProgress","진행 상태 조회 중",30,250,475,105,20);
            editor.editButton=Button(profile.transform,"UiNextEdit","프로필 편집",70,389,187,47);
            FixProfileText(profile);
        }
        static void ScrollbarFor(ScrollRect scroll,float width,float height)
        {
            if(scroll==null || scroll.verticalScrollbar!=null)return;
            var track=Rect(scroll.transform,"Scrollbar",width-12,0,12,height);track.gameObject.AddComponent<Image>().color=new Color32(226,209,183,255);
            var handle=Rect(track,"Handle",0,0,12,height);Stretch(handle);var graphic=handle.gameObject.AddComponent<Image>();graphic.color=new Color32(141,111,81,255);
            var bar=track.gameObject.AddComponent<Scrollbar>();bar.handleRect=handle;bar.targetGraphic=graphic;bar.direction=Scrollbar.Direction.BottomToTop;
            scroll.verticalScrollbar=bar;scroll.verticalScrollbarVisibility=ScrollRect.ScrollbarVisibility.AutoHide;
        }
        static TMP_InputField Input(Transform parent,string name,string placeholder,float x,float y,float w,float h,bool multiline)
        {
            var rect=Rect(parent,name,x,y,w,h);var bg=rect.gameObject.AddComponent<Image>();bg.color=new Color32(255,250,238,255);
            var input=rect.gameObject.AddComponent<TMP_InputField>();input.targetGraphic=bg;
            var viewport=Rect(rect,"Viewport",16,10,w-32,h-20);viewport.gameObject.AddComponent<RectMask2D>();
            var text=Label(viewport,"Text","",0,0,w-32,h-20,27);text.overflowMode=TextOverflowModes.Overflow;
            var hint=Label(viewport,"Placeholder",placeholder,0,0,w-32,h-20,25);hint.color=new Color32(130,112,94,255);
            input.textViewport=viewport;input.textComponent=(TextMeshProUGUI)text;input.placeholder=hint;
            input.lineType=multiline?TMP_InputField.LineType.MultiLineNewline:TMP_InputField.LineType.SingleLine;
            input.richText=false;input.characterLimit=0;input.onFocusSelectAll=false;
            input.caretColor=Ink;input.customCaretColor=true;input.selectionColor=new Color(.85f,.65f,.4f,.5f);
            return input;
        }
        static GameObject Modal(Transform parent,string name)
        {var r=Rect(parent,name,0,0,1920,1080);r.anchorMin=Vector2.zero;r.anchorMax=Vector2.one;r.offsetMin=r.offsetMax=Vector2.zero;var bg=r.gameObject.AddComponent<Image>();bg.color=new Color(.12f,.08f,.05f,.7f);return r.gameObject;}
        static RectTransform PaperCard(Transform parent,string name,float x,float y,float w,float h)
        {var rect=Rect(parent,name,x,y,w,h);var image=rect.gameObject.AddComponent<Image>();image.sprite=Paper;image.type=Image.Type.Sliced;return rect;}
        static Button Button(Transform parent,string name,string caption,float x,float y,float w,float h)
        {var rect=Rect(parent,name,x,y,w,h);var image=rect.gameObject.AddComponent<Image>();image.color=new Color32(221,193,153,255);var button=rect.gameObject.AddComponent<Button>();button.targetGraphic=image;
            var colors=button.colors;colors.highlightedColor=new Color(1,.94f,.84f);colors.pressedColor=new Color(.8f,.7f,.6f);button.colors=colors;
            var text=Label(rect,"Label",caption,4,2,w-8,h-4,22);text.alignment=TextAlignmentOptions.Center;return button;}
        static TMP_Text Label(Transform parent,string name,string value,float x,float y,float w,float h,int size)
        {var text=Rect(parent,name,x,y,w,h).gameObject.AddComponent<TextMeshProUGUI>();text.font=Font;text.fontSize=size;text.color=Ink;text.richText=false;text.text=value;text.raycastTarget=false;text.alignment=TextAlignmentOptions.TopLeft;return text;}
        static RectTransform Rect(Transform parent,string name,float x,float y,float w,float h)
        {var r=new GameObject(name,typeof(RectTransform)).GetComponent<RectTransform>();r.SetParent(parent,false);Place(r,x,y,w,h);return r;}
        static void Stretch(RectTransform r) { r.anchorMin=Vector2.zero; r.anchorMax=Vector2.one; r.offsetMin=r.offsetMax=Vector2.zero; }
        static void Place(RectTransform r,float x,float y,float w,float h)
        {r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=new Vector2(x,-y);r.sizeDelta=new Vector2(w,h);}
    }
}
