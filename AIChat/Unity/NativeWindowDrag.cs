using UnityEngine;
using UnityEngine.EventSystems;

namespace AIChat.Unity
{
    public sealed class NativeWindowDrag : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        public RectTransform Target;
        private Vector2 _offset;
        private Camera _camera;
        private bool _dragging;

        public void OnPointerDown(PointerEventData e)
        {
            if (Target == null || e.button != PointerEventData.InputButton.Left) return;
            _camera = e.pressEventCamera;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(Target.parent as RectTransform, e.position, _camera, out var point);
            _offset = Target.anchoredPosition - point;
            _dragging = true;
        }

        public void OnPointerUp(PointerEventData e) { _dragging = false; }
        private void OnDisable() { _dragging = false; }

        private void Update()
        {
            if (!_dragging || Target == null) return;
            if (!Input.GetMouseButton(0)) { _dragging = false; return; }
            RectTransformUtility.ScreenPointToLocalPointInRectangle(Target.parent as RectTransform, Input.mousePosition, _camera, out var point);
            Target.anchoredPosition = point + _offset;
            Clamp(Target);
        }

        public static void Clamp(RectTransform target)
        {
            var parent = target.parent as RectTransform;
            if (parent == null) return;
            float halfRange = Mathf.Max(0, (parent.rect.height - target.rect.height) / 2 - 12);
            target.anchoredPosition = new Vector2(Mathf.Clamp(target.anchoredPosition.x, 12, Mathf.Max(12, parent.rect.width - target.rect.width - 12)),
                Mathf.Clamp(target.anchoredPosition.y, -halfRange, halfRange));
        }
    }
}
