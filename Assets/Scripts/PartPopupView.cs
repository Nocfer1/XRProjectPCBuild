using TMPro;
using UnityEngine;
using UnityEngine.UI;

// One world-space popup, built in code so no prefab is needed. A card shows a title and body text;
// a tag is a single compact line. Canvas units are scaled by PartPopupManager to keep a constant
// apparent size at any distance.
public class PartPopupView : MonoBehaviour
{
    private const float CardWidth = 280f;
    private static readonly Color BackgroundColor = new Color(0.07f, 0.08f, 0.1f, 0.85f);
    private static readonly Color TitleColor = new Color(0.55f, 0.82f, 1f);
    private static readonly Color BodyColor = new Color(0.9f, 0.92f, 0.95f);

    private Canvas canvas;
    private CanvasGroup group;
    private RectTransform rect;
    private TextMeshProUGUI title;
    private TextMeshProUGUI body;

    public bool IsCard { get; private set; }
    public float WidthUnits => rect.rect.width;

    public float Alpha
    {
        get => group.alpha;
        set
        {
            group.alpha = value;
            canvas.enabled = value > 0.001f;
        }
    }

    public static PartPopupView Create(Transform parent, bool card)
    {
        var go = new GameObject(card ? "Part Info Card" : "Part Info Tag", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var view = go.AddComponent<PartPopupView>();
        view.Build(card);
        return view;
    }

    private void Build(bool card)
    {
        IsCard = card;
        rect = (RectTransform)transform;
        canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        group = gameObject.AddComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false;

        var background = gameObject.AddComponent<Image>();
        background.color = BackgroundColor;
        background.raycastTarget = false;

        var layout = gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = card ? new RectOffset(18, 18, 14, 16) : new RectOffset(12, 12, 6, 7);
        layout.spacing = 6f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var fitter = gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.horizontalFit = card ? ContentSizeFitter.FitMode.Unconstrained : ContentSizeFitter.FitMode.PreferredSize;
        if (card) rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, CardWidth);

        title = CreateText("Title", card ? 24f : 18f, card ? TitleColor : BodyColor, FontStyles.Bold);
        if (card) body = CreateText("Body", 16f, BodyColor, FontStyles.Normal);
        Alpha = 0f;
    }

    private TextMeshProUGUI CreateText(string objectName, float size, Color color, FontStyles style)
    {
        var go = new GameObject(objectName, typeof(RectTransform));
        go.transform.SetParent(transform, false);
        var text = go.AddComponent<TextMeshProUGUI>();
        text.fontSize = size;
        text.color = color;
        text.fontStyle = style;
        text.raycastTarget = false;
        text.textWrappingMode = IsCard ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        return text;
    }

    public void SetContent(string titleText, string bodyText = null)
    {
        // Tags are refreshed every frame; only rebuild the layout when the text actually changes.
        if (title.text == titleText && (body == null || body.text == (bodyText ?? string.Empty))) return;
        title.text = titleText;
        if (body != null)
        {
            body.text = bodyText ?? string.Empty;
            body.gameObject.SetActive(!string.IsNullOrEmpty(bodyText));
        }
        LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
    }

    public void Place(Vector3 position, Quaternion rotation, float metresPerUnit)
    {
        transform.SetPositionAndRotation(position, rotation);
        transform.localScale = Vector3.one * metresPerUnit;
    }
}
