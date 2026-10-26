using System;
using AIChat.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AIChat.Unity
{
    public sealed class NativeWindowResize : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public RectTransform Target;
        public ResizeEdges Edges;
        public Image Indicator;
        public Action Started;
        public Action Finished;
        private readonly Color _idle = new Color(0.55f, 0.52f, 0.68f, 0.55f);
        private readonly Color _active = new Color(0.42f, 0.82f, 0.86f);
        private bool _resizing;
        private bool _hovered;
        private Camera _camera;
        private RectTransform _canvas;
        private Vector2 _pointerStart;
        private Vector2 _canvasSize;
        private WindowBounds _start;

        public void OnPointerDown(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left || Target == null) return;
            _canvas = Target.parent as RectTransform;
            if (_canvas == null) return;
            _camera = e.pressEventCamera;
            _canvasSize = _canvas.rect.size;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvas, e.position, _camera, out _pointerStart);
            _start = new WindowBounds(Target.anchoredPosition.x,
                _canvasSize.y / 2 + Target.anchoredPosition.y - Target.rect.height / 2, Target.rect.width, Target.rect.height);
            _resizing = true;
            UpdateIndicator();
            Started?.Invoke();
        }

        private void Update()
        {
            if (!_resizing) return;
            if (!Input.GetMouseButton(0) || _canvas == null || Target == null || _canvas.rect.size != _canvasSize)
            {
                Finish();
                return;
            }
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvas, Input.mousePosition, _camera, out var pointer);
            Vector2 delta = pointer - _pointerStart;
            var bounds = WindowResizeGeometry.Resize(_start, delta.x, delta.y, Edges, _canvasSize.x, _canvasSize.y);
            Target.sizeDelta = new Vector2(bounds.Width, bounds.Height);
            Target.anchoredPosition = new Vector2(bounds.Left, bounds.Bottom + bounds.Height / 2 - _canvasSize.y / 2);
        }

        public void OnPointerUp(PointerEventData e) { Finish(); }
        public void OnPointerEnter(PointerEventData e) { _hovered = true; UpdateIndicator(); }
        public void OnPointerExit(PointerEventData e) { _hovered = false; UpdateIndicator(); }
        private void OnEnable() { UpdateIndicator(); }
        private void OnDisable() { _hovered = false; Finish(); }
        private void UpdateIndicator() { if (Indicator != null) Indicator.color = _resizing || _hovered ? _active : _idle; }
        private void Finish()
        {
            if (!_resizing) return;
            _resizing = false;
            UpdateIndicator();
            Finished?.Invoke();
        }
    }
}
