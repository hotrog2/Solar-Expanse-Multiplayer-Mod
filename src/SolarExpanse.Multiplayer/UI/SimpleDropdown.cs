using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolarExpanse.Multiplayer.UI;

internal sealed class SimpleDropdown
{
    private readonly GameObject _root;
    private readonly TextMeshProUGUI _caption;
    private readonly RectTransform _listRect;
    private readonly GameObject _listRoot;
    private readonly Button _button;
    private readonly Action<string> _onChanged;
    private List<string> _options = new List<string>();

    public SimpleDropdown(Transform parent, string label, Vector2 position, Vector2 size, IEnumerable<string> options, string selected, Action<string> onChanged)
    {
        _onChanged = onChanged;

        MainMenuMultiplayerPanel.CreateText(parent, label, position + new Vector2(0f, 32f), new Vector2(size.x, 24f), 15, FontStyles.Bold, new Color(0f, 1f, 0.88f));

        _root = new GameObject(label + " Dropdown");
        _root.transform.SetParent(parent, worldPositionStays: false);
        var rect = _root.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        var image = _root.AddComponent<Image>();
        image.color = new Color(1f, 1f, 1f, 0.95f);

        _button = _root.AddComponent<Button>();
        _button.onClick.AddListener(ToggleList);

        _caption = MainMenuMultiplayerPanel.CreateText(_root.transform, selected, Vector2.zero, size - new Vector2(18f, 8f), 17, FontStyles.Normal, Color.black);
        _caption.alignment = TextAlignmentOptions.MidlineLeft;
        _caption.rectTransform.anchorMin = Vector2.zero;
        _caption.rectTransform.anchorMax = Vector2.one;
        _caption.rectTransform.offsetMin = new Vector2(9f, 4f);
        _caption.rectTransform.offsetMax = new Vector2(-28f, -4f);

        var arrow = MainMenuMultiplayerPanel.CreateText(_root.transform, "v", new Vector2(size.x * 0.5f - 18f, 0f), new Vector2(24f, size.y), 18, FontStyles.Bold, Color.black);
        arrow.alignment = TextAlignmentOptions.Center;

        _listRoot = new GameObject(label + " Dropdown List");
        _listRoot.transform.SetParent(parent, worldPositionStays: false);
        _listRect = _listRoot.AddComponent<RectTransform>();
        _listRect.anchorMin = new Vector2(0.5f, 0.5f);
        _listRect.anchorMax = new Vector2(0.5f, 0.5f);
        _listRect.pivot = new Vector2(0.5f, 1f);
        _listRect.anchoredPosition = position + new Vector2(0f, -size.y * 0.5f - 2f);
        _listRect.sizeDelta = new Vector2(size.x, 0f);
        _listRoot.SetActive(false);

        SetOptions(options, selected);
    }

    public string Selected { get; private set; } = string.Empty;

    public void SetOptions(IEnumerable<string> options, string selected)
    {
        var nextOptions = new List<string>(options);
        if (nextOptions.Count == 0)
        {
            nextOptions.Add("0");
        }

        var optionsChanged = _options.Count != nextOptions.Count || !_options.SequenceEqual(nextOptions);
        _options = nextOptions;
        Selected = _options.Contains(selected) ? selected : _options[0];
        _caption.text = Selected;
        if (optionsChanged)
        {
            RebuildList();
        }
    }

    public void SetSelected(string selected)
    {
        if (!_options.Contains(selected))
        {
            return;
        }

        Selected = selected;
        _caption.text = selected;
    }

    public void SetInteractable(bool interactable)
    {
        _button.interactable = interactable;
        _caption.color = interactable ? Color.black : new Color(0.35f, 0.35f, 0.35f);
        if (!interactable)
        {
            _listRoot.SetActive(false);
        }
    }

    private void ToggleList()
    {
        if (!_button.interactable)
        {
            return;
        }

        _listRoot.SetActive(!_listRoot.activeSelf);
        _listRoot.transform.SetAsLastSibling();
    }

    private void RebuildList()
    {
        for (var i = _listRoot.transform.childCount - 1; i >= 0; i--)
        {
            UnityEngine.Object.Destroy(_listRoot.transform.GetChild(i).gameObject);
        }

        const float itemHeight = 32f;
        _listRect.sizeDelta = new Vector2(_listRect.sizeDelta.x, itemHeight * _options.Count);

        for (var index = 0; index < _options.Count; index++)
        {
            var option = _options[index];
            var item = new GameObject(option + " Option");
            item.transform.SetParent(_listRoot.transform, worldPositionStays: false);
            var rect = item.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -itemHeight * index);
            rect.sizeDelta = new Vector2(0f, itemHeight);

            var image = item.AddComponent<Image>();
            image.color = new Color(0.92f, 0.92f, 0.92f, 0.98f);

            var button = item.AddComponent<Button>();
            button.onClick.AddListener(() =>
            {
                Selected = option;
                _caption.text = option;
                _listRoot.SetActive(false);
                _onChanged(option);
            });

            var text = MainMenuMultiplayerPanel.CreateText(item.transform, option, Vector2.zero, new Vector2(_listRect.sizeDelta.x - 18f, itemHeight), 15, FontStyles.Normal, Color.black);
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(9f, 2f);
            text.rectTransform.offsetMax = new Vector2(-9f, -2f);
        }
    }
}
