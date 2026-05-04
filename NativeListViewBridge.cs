using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WFListView = System.Windows.Forms.ListView;
using WFListViewItem = System.Windows.Forms.ListViewItem;

namespace UltraVideoEditor
{
    /// <summary>
    /// Emulira WPF ListBox API nad Win32 nativeListView.
    /// Sve metode i propertiji koji se pozivaju kao lstTimeline.X
    /// rade transparentno nad Win32 kontrolom koju JAWS nativno cita.
    /// </summary>
    public class NativeListViewBridge
    {
        private readonly WFListView _lv;
        private List<TimelineItem> _source;

        public NativeListViewBridge(WFListView listView, List<TimelineItem> source)
        {
            _lv     = listView;
            _source = source;
        }

        // Poziva se kad se _source promeni (nova lista)
        public void SetSource(List<TimelineItem> source)
        {
            _source = source;
        }

        // ── SelectedItem ───────────────────────────────────────────
        public object SelectedItem
        {
            get
            {
                if (_lv == null || _lv.SelectedItems.Count == 0) return null;
                return _lv.SelectedItems[0].Tag as TimelineItem;
            }
        }

        // ── SelectedIndex ──────────────────────────────────────────
        public int SelectedIndex
        {
            get
            {
                if (_lv == null || _lv.SelectedItems.Count == 0) return -1;
                return _lv.SelectedItems[0].Index;
            }
            set
            {
                if (_lv == null) return;
                foreach (WFListViewItem item in _lv.Items)
                    item.Selected = false;
                if (value >= 0 && value < _lv.Items.Count)
                {
                    _lv.Items[value].Selected  = true;
                    _lv.Items[value].Focused   = true;
                    _lv.Items[value].EnsureVisible();
                }
            }
        }

        // ── Items (read-only collection wrapper) ────────────────────
        public NativeBridgeItemCollection Items => new NativeBridgeItemCollection(_lv, _source);

        // ── SelectedItems (multi-select) ────────────────────────────
        public NativeBridgeSelectedItems SelectedItems => new NativeBridgeSelectedItems(_lv);

        // ── Focus ───────────────────────────────────────────────────
        public bool Focus()
        {
            if (_lv == null) return false;
            _lv.Focus();
            return true;
        }

        // ── IsFocused / IsKeyboardFocusWithin ───────────────────────
        public bool IsFocused             => _lv != null && _lv.Focused;
        public bool IsKeyboardFocusWithin => _lv != null && _lv.ContainsFocus;

        // ── AllowDrop ───────────────────────────────────────────────
        public bool AllowDrop
        {
            get => _lv?.AllowDrop ?? false;
            set { if (_lv != null) _lv.AllowDrop = value; }
        }

        // ── Drop event (WinForms DragDrop -> WPF bridge) ────────────
        public event System.Windows.DragEventHandler Drop
        {
            add    { /* WinForms drag nema direktan WPF ekvivalent - ignorise se */ }
            remove { }
        }

        // ── ContextMenu (ignorise se - nativeListView ima ContextMenuStrip) ──
        public ContextMenu ContextMenu { get; set; }

        // ── PreviewKeyDown (ignorise se - nativeListView.KeyDown se koristi) ──
        public event System.Windows.Input.KeyEventHandler PreviewKeyDown
        {
            add    { }
            remove { }
        }

        // ── ScrollIntoView ──────────────────────────────────────────
        public void ScrollIntoView(object item)
        {
            if (_lv == null || item == null) return;
            foreach (WFListViewItem lvi in _lv.Items)
            {
                if (lvi.Tag == item)
                {
                    lvi.EnsureVisible();
                    return;
                }
            }
        }

        // ── ItemContainerGenerator (stub - nije potreban uz Win32) ──
        public NativeBridgeContainerGenerator ItemContainerGenerator
            => new NativeBridgeContainerGenerator();
    }

    // ── Kolekcija stavki ─────────────────────────────────────────────
    public class NativeBridgeItemCollection
    {
        private readonly WFListView _lv;
        private readonly List<TimelineItem> _source;

        public NativeBridgeItemCollection(WFListView lv, List<TimelineItem> source)
        {
            _lv     = lv;
            _source = source;
        }

        public int Count => _lv?.Items.Count ?? 0;

        public object this[int index]
        {
            get
            {
                if (_lv == null || index < 0 || index >= _lv.Items.Count) return null;
                return _lv.Items[index].Tag;
            }
        }

        public void Clear()
        {
            _lv?.Items.Clear();
        }

        public void Add(object item)
        {
            // Ovo ne koristimo direktno - UpdateNativeListView radi punjenje
        }

        public bool Contains(object item)
        {
            if (_lv == null) return false;
            foreach (WFListViewItem lvi in _lv.Items)
                if (lvi.Tag == item) return true;
            return false;
        }

        public IEnumerator GetEnumerator()
        {
            var list = new List<object>();
            if (_lv != null)
                foreach (WFListViewItem lvi in _lv.Items)
                    if (lvi.Tag != null) list.Add(lvi.Tag);
            return list.GetEnumerator();
        }
    }

    // ── Selektovane stavke ────────────────────────────────────────────
    public class NativeBridgeSelectedItems
    {
        private readonly WFListView _lv;
        public NativeBridgeSelectedItems(WFListView lv) { _lv = lv; }

        public int Count => _lv?.SelectedItems.Count ?? 0;

        public void Clear()
        {
            if (_lv == null) return;
            foreach (WFListViewItem item in _lv.Items)
                item.Selected = false;
        }

        public void Add(object item)
        {
            if (_lv == null) return;
            foreach (WFListViewItem lvi in _lv.Items)
                if (lvi.Tag == item) { lvi.Selected = true; return; }
        }
    }

    // ── Container generator stub ──────────────────────────────────────
    public class NativeBridgeContainerGenerator
    {
        public object ContainerFromIndex(int index) => null;
        public object ContainerFromItem(object item) => null;
    }
}
