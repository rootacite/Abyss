import os
import sys
import re
import json
import zipfile
import shutil
import argparse

try:
    from PyQt6.QtCore import (
        QEvent,
        QObject,
        QRect,
        QRunnable,
        QSize,
        Qt,
        QThreadPool,
        QTimer,
        pyqtSignal,
    )
    from PyQt6.QtGui import (
        QColor,
        QImage,
        QImageReader,
        QKeySequence,
        QPainter,
        QPen,
        QPixmap,
        QShortcut,
    )
    from PyQt6.QtWidgets import (
        QAbstractItemView,
        QApplication,
        QDialog,
        QHBoxLayout,
        QInputDialog,
        QLabel,
        QListWidget,
        QListWidgetItem,
        QPushButton,
        QScrollArea,
        QSizePolicy,
        QSplitter,
        QVBoxLayout,
        QWidget,
    )
except ImportError:
    print(
        "Error: PyQt6 is required for the bookmark editor.\n"
        "Install it globally with:\n"
        "  paru -S python-pyqt6 qt6-imageformats"
    )
    sys.exit(1)

# Supported image file extensions
SUPPORTED_EXTENSIONS = ('.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp')

THUMB_MIN_H = 48
THUMB_DEFAULT_H = 96
STRIP_MIN_H = 80
STRIP_DEFAULT_H = 140
THUMB_PREFETCH = 10
THUMB_POOL_THREADS = 2
THUMB_ASPECT = 0.70
CELL_PAD = 4
CELL_GAP = 4
CAPTION_H = 16


def natural_sort_key(s):
    """Natural sort algorithm to ensure 2.webp comes before 10.webp."""
    basename = os.path.basename(s)
    return [int(text) if text.isdigit() else text.lower()
            for text in re.split('([0-9]+)', basename)]


class ImageView(QLabel):
    """QLabel that keeps a source pixmap scaled to the current widget size."""

    def __init__(self, parent=None):
        super().__init__(parent)
        self._source = None
        self.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.setMinimumSize(320, 160)
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        self.setStyleSheet("QLabel { background-color: #1e1e1e; color: #cccccc; }")
        self._smooth_timer = QTimer(self)
        self._smooth_timer.setSingleShot(True)
        self._smooth_timer.setInterval(80)
        self._smooth_timer.timeout.connect(lambda: self._rescale(smooth=True))

    def set_source(self, pixmap):
        self._source = pixmap if pixmap and not pixmap.isNull() else None
        self._rescale(smooth=True)

    def clear_source(self, message="Failed to load image"):
        self._source = None
        self.setPixmap(QPixmap())
        self.setText(message)

    def resizeEvent(self, event):
        super().resizeEvent(event)
        self._rescale(smooth=False)
        self._smooth_timer.start()

    def _rescale(self, smooth=True):
        if self._source is None:
            return
        self.setText("")
        mode = (
            Qt.TransformationMode.SmoothTransformation
            if smooth
            else Qt.TransformationMode.FastTransformation
        )
        scaled = self._source.scaled(
            self.size(),
            Qt.AspectRatioMode.KeepAspectRatio,
            mode,
        )
        self.setPixmap(scaled)


class _ThumbSignals(QObject):
    decoded = pyqtSignal(int, QImage, int, int)


class _ThumbTask(QRunnable):
    """Decode one thumbnail off the GUI thread."""

    def __init__(self, index, path, width, height, signals):
        super().__init__()
        self.setAutoDelete(True)
        self._index = index
        self._path = path
        self._width = width
        self._height = height
        self._signals = signals

    def run(self):
        reader = QImageReader(self._path)
        reader.setAutoTransform(True)
        size = reader.size()
        target = QSize(max(1, self._width), max(1, self._height))
        if size.isValid():
            reader.setScaledSize(size.scaled(target, Qt.AspectRatioMode.KeepAspectRatio))
        image = reader.read()
        try:
            self._signals.decoded.emit(self._index, image, self._width, self._height)
        except RuntimeError:
            pass


class _FilmTrack(QWidget):
    """Single virtualized strip: paints only the cells in the dirty rect."""

    def __init__(self, strip):
        super().__init__()
        self._strip = strip
        self.setCursor(Qt.CursorShape.PointingHandCursor)

    def paintEvent(self, event):
        self._strip.paint_track(self, event)


class ThumbnailStrip(QScrollArea):
    """Horizontally scrolling filmstrip for jumping to a page."""

    page_selected = pyqtSignal(int)

    def __init__(self, image_dir, filenames, parent=None):
        # QAbstractScrollArea installs this object as the viewport event
        # filter during super().__init__, so these must exist first.
        self._track = None
        self._reload_timer = None
        super().__init__(parent)
        self.image_dir = image_dir
        self.filenames = filenames
        self._current = 0
        self._cache = {}
        self._inflight = set()
        self._thumb_w = int(THUMB_DEFAULT_H * THUMB_ASPECT)
        self._thumb_h = THUMB_DEFAULT_H
        self._cell_w = self._thumb_w + CELL_PAD * 2
        self._cell_h = self._thumb_h + CELL_PAD + CAPTION_H
        self._press_x = None
        self._press_scroll = 0
        self._dragged = False

        self.setFocusPolicy(Qt.FocusPolicy.NoFocus)
        self.setWidgetResizable(False)
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)
        self.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.setMinimumHeight(STRIP_MIN_H)
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        self.setStyleSheet(
            "QScrollArea { background-color: #1a1a1a; border: 1px solid #333; }"
        )

        self._track = _FilmTrack(self)
        self._track.installEventFilter(self)
        self.setWidget(self._track)

        self._signals = _ThumbSignals(self)
        self._signals.decoded.connect(self._on_decoded)
        self._pool = QThreadPool(self)
        self._pool.setMaxThreadCount(THUMB_POOL_THREADS)
        QImageReader.supportedImageFormats()

        self._reload_timer = QTimer(self)
        self._reload_timer.setSingleShot(True)
        self._reload_timer.setInterval(60)
        self._reload_timer.timeout.connect(self._request_visible)

        self.horizontalScrollBar().valueChanged.connect(self._request_visible)

    def sizeHint(self):
        return QSize(640, STRIP_DEFAULT_H)

    def showEvent(self, event):
        super().showEvent(event)
        self._apply_metrics()
        self._request_visible()

    def resizeEvent(self, event):
        super().resizeEvent(event)
        if self._reload_timer is None or self._track is None:
            return
        self._apply_metrics()
        self._reload_timer.start()

    def eventFilter(self, obj, event):
        if self._track is None or obj is not self._track:
            return super().eventFilter(obj, event)
        etype = event.type()
        if etype == QEvent.Type.MouseButtonPress and event.button() == Qt.MouseButton.LeftButton:
            self._press_x = event.globalPosition().x()
            self._press_scroll = self.horizontalScrollBar().value()
            self._dragged = False
            return False
        if etype == QEvent.Type.MouseMove and self._press_x is not None:
            dx = int(self._press_x - event.globalPosition().x())
            if abs(dx) >= 5:
                self._dragged = True
                self.horizontalScrollBar().setValue(self._press_scroll + dx)
                return True
            return False
        if etype == QEvent.Type.MouseButtonRelease and event.button() == Qt.MouseButton.LeftButton:
            dragged = self._dragged
            self._press_x = None
            self._dragged = False
            if dragged:
                return True
            index = self._index_at(event.position().x())
            if index is not None:
                self.page_selected.emit(index)
            return True
        return super().eventFilter(obj, event)

    def wheelEvent(self, event):
        bar = self.horizontalScrollBar()
        pixel = event.pixelDelta()
        angle = event.angleDelta()
        delta = pixel.x() or pixel.y() or angle.x() or angle.y()
        bar.setValue(bar.value() - delta)
        event.accept()

    def set_current(self, index):
        if index == self._current:
            self._ensure_current_visible()
            return
        prev = self._current
        self._current = index
        self._track.update(self._cell_rect(prev))
        self._track.update(self._cell_rect(index))
        self._ensure_current_visible()
        self._request_visible()

    def paint_track(self, track, event):
        painter = QPainter(track)
        painter.fillRect(event.rect(), QColor("#1a1a1a"))
        start, end = self._range_for_rect(event.rect())
        for index in range(start, end + 1):
            self._paint_cell(painter, index)

    def _apply_metrics(self):
        vh = max(STRIP_MIN_H - 16, self.viewport().height())
        thumb_h = max(THUMB_MIN_H, vh - CELL_PAD * 2 - CAPTION_H)
        thumb_w = max(28, int(thumb_h * THUMB_ASPECT))
        self._thumb_w = thumb_w
        self._thumb_h = thumb_h
        self._cell_w = thumb_w + CELL_PAD * 2
        self._cell_h = thumb_h + CELL_PAD + CAPTION_H
        count = len(self.filenames)
        width = CELL_GAP * 2
        if count:
            width += count * self._cell_w + (count - 1) * CELL_GAP
        self._track.setFixedSize(max(width, self.viewport().width()), max(1, vh))
        self._track.update()

    def _thumb_px(self):
        dpr = self.devicePixelRatio() or 1.0
        return max(1, int(self._thumb_w * dpr)), max(1, int(self._thumb_h * dpr))

    def _cell_origin(self, index):
        return CELL_GAP + index * (self._cell_w + CELL_GAP), CELL_GAP

    def _cell_rect(self, index):
        x, y = self._cell_origin(index)
        return QRect(x, y, self._cell_w, self._cell_h)

    def _index_at(self, x):
        local = x - CELL_GAP
        if local < 0:
            return None
        stride = self._cell_w + CELL_GAP
        if stride <= 0:
            return None
        index = int(local // stride)
        if local % stride > self._cell_w:
            return None
        if 0 <= index < len(self.filenames):
            return index
        return None

    def _visible_range(self, prefetch=0):
        if not self.filenames:
            return 0, -1
        x = self.horizontalScrollBar().value()
        w = max(1, self.viewport().width())
        start, end = self._range_for_rect(QRect(x, 0, w, self._cell_h))
        start = max(0, start - prefetch)
        end = min(len(self.filenames) - 1, end + prefetch)
        return start, end

    def _range_for_rect(self, rect):
        if not self.filenames:
            return 0, -1
        stride = self._cell_w + CELL_GAP
        start = max(0, (rect.left() - CELL_GAP) // stride)
        end = min(len(self.filenames) - 1, (rect.right() - CELL_GAP) // stride)
        return int(start), int(end)

    def _ensure_current_visible(self):
        rect = self._cell_rect(self._current)
        self.ensureVisible(rect.center().x(), rect.center().y(), 80, 0)

    def _request_visible(self):
        if not self.filenames or not self.isVisible():
            return
        tw, th = self._thumb_px()
        start, end = self._visible_range(prefetch=THUMB_PREFETCH)
        for index in range(start, end + 1):
            job = (index, tw, th)
            cached = self._cache.get(index)
            if cached and cached["w"] == tw and cached["h"] == th:
                continue
            if job in self._inflight:
                continue
            self._inflight.add(job)
            task = _ThumbTask(
                index,
                os.path.join(self.image_dir, self.filenames[index]),
                tw,
                th,
                self._signals,
            )
            self._pool.start(task)

    def _on_decoded(self, index, image, width, height):
        self._inflight.discard((index, width, height))
        if image.isNull():
            return
        tw, th = self._thumb_px()
        cached = self._cache.get(index)
        if (width, height) != (tw, th) and cached and cached["w"] == tw and cached["h"] == th:
            return
        pixmap = QPixmap.fromImage(image)
        pixmap.setDevicePixelRatio(self.devicePixelRatio() or 1.0)
        self._cache[index] = {"pix": pixmap, "w": width, "h": height}
        self._track.update(self._cell_rect(index))

    def _paint_cell(self, painter, index):
        cell = self._cell_rect(index)
        selected = index == self._current
        painter.fillRect(cell, QColor("#252525"))
        painter.setPen(QPen(QColor("#5aa8ff" if selected else "#3a3a3a"), 2))
        painter.drawRect(cell.adjusted(1, 1, -2, -2))

        thumb = QRect(
            cell.x() + CELL_PAD,
            cell.y() + CELL_PAD,
            self._thumb_w,
            self._thumb_h,
        )
        painter.fillRect(thumb, QColor("#111111"))
        cached = self._cache.get(index)
        if cached and not cached["pix"].isNull():
            pix = cached["pix"]
            dpr = pix.devicePixelRatio() or 1.0
            logical = QSize(max(1, round(pix.width() / dpr)), max(1, round(pix.height() / dpr)))
            fitted = logical.scaled(thumb.size(), Qt.AspectRatioMode.KeepAspectRatio)
            dest = QRect(
                thumb.x() + (thumb.width() - fitted.width()) // 2,
                thumb.y() + (thumb.height() - fitted.height()) // 2,
                fitted.width(),
                fitted.height(),
            )
            painter.drawPixmap(dest, pix)
        else:
            painter.setPen(QColor("#888888"))
            painter.drawText(thumb, Qt.AlignmentFlag.AlignCenter, str(index + 1))

        caption = QRect(cell.x(), thumb.bottom(), cell.width(), CAPTION_H)
        painter.setPen(QColor("#ffffff" if selected else "#bbbbbb"))
        painter.drawText(caption, Qt.AlignmentFlag.AlignCenter, str(index + 1))


class BookmarkEditorDialog(QDialog):
    """Page viewer plus bookmark list for one extracted comic."""

    def __init__(self, image_dir, filenames, comic_name, parent=None):
        super().__init__(parent)
        self.image_dir = image_dir
        self.filenames = filenames
        self.comic_name = comic_name
        self.bookmarks = []
        self.current_index = 0

        self.setWindowTitle(f"Bookmarks — {comic_name}")
        self.resize(1280, 840)

        self.image_view = ImageView()
        self.page_label = QLabel()
        self.page_label.setAlignment(Qt.AlignmentFlag.AlignCenter)

        prev_btn = QPushButton("Previous")
        next_btn = QPushButton("Next")
        prev_btn.clicked.connect(self.show_previous)
        next_btn.clicked.connect(self.show_next)

        nav = QHBoxLayout()
        nav.addWidget(prev_btn)
        nav.addWidget(self.page_label, 1)
        nav.addWidget(next_btn)

        self.thumb_strip = ThumbnailStrip(image_dir, filenames)
        self.thumb_strip.page_selected.connect(self._on_thumb_clicked)

        top_pane = QWidget()
        top_layout = QVBoxLayout(top_pane)
        top_layout.setContentsMargins(0, 0, 0, 0)
        top_layout.addWidget(self.image_view, 1)
        top_layout.addLayout(nav)

        vsplit = QSplitter(Qt.Orientation.Vertical)
        vsplit.addWidget(top_pane)
        vsplit.addWidget(self.thumb_strip)
        vsplit.setChildrenCollapsible(False)
        vsplit.setStretchFactor(0, 1)
        vsplit.setStretchFactor(1, 0)
        vsplit.setHandleWidth(8)
        vsplit.setSizes([720, STRIP_DEFAULT_H])
        vsplit.setStyleSheet(
            "QSplitter::handle:vertical { background: #3a3a3a; }"
            "QSplitter::handle:vertical:hover { background: #5aa8ff; }"
        )

        viewer = QWidget()
        viewer_layout = QVBoxLayout(viewer)
        viewer_layout.setContentsMargins(0, 0, 0, 0)
        viewer_layout.addWidget(vsplit, 1)

        self.list_widget = QListWidget()
        self.list_widget.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.list_widget.itemClicked.connect(self._on_bookmark_clicked)

        add_btn = QPushButton("Add")
        rename_btn = QPushButton("Rename")
        delete_btn = QPushButton("Delete")
        up_btn = QPushButton("Move Up")
        down_btn = QPushButton("Move Down")
        add_btn.clicked.connect(self.add_bookmark)
        rename_btn.clicked.connect(self.rename_bookmark)
        delete_btn.clicked.connect(self.delete_bookmark)
        up_btn.clicked.connect(lambda: self._move_bookmark(-1))
        down_btn.clicked.connect(lambda: self._move_bookmark(1))

        edit_row = QHBoxLayout()
        edit_row.addWidget(add_btn)
        edit_row.addWidget(rename_btn)
        edit_row.addWidget(delete_btn)

        order_row = QHBoxLayout()
        order_row.addWidget(up_btn)
        order_row.addWidget(down_btn)

        side = QWidget()
        side.setMinimumWidth(280)
        side_layout = QVBoxLayout(side)
        side_layout.setContentsMargins(0, 0, 0, 0)
        side_layout.addWidget(QLabel("Bookmarks"))
        side_layout.addWidget(self.list_widget, 1)
        side_layout.addLayout(edit_row)
        side_layout.addLayout(order_row)

        splitter = QSplitter(Qt.Orientation.Horizontal)
        splitter.addWidget(viewer)
        splitter.addWidget(side)
        splitter.setStretchFactor(0, 4)
        splitter.setStretchFactor(1, 1)
        splitter.setSizes([960, 320])

        save_btn = QPushButton("Save")
        save_btn.clicked.connect(self.accept)
        save_row = QHBoxLayout()
        save_row.addStretch(1)
        save_row.addWidget(save_btn)

        root = QVBoxLayout(self)
        root.addWidget(splitter, 1)
        root.addLayout(save_row)

        QShortcut(QKeySequence(Qt.Key.Key_Left), self, self.show_previous)
        QShortcut(QKeySequence(Qt.Key.Key_Right), self, self.show_next)
        QShortcut(QKeySequence("A"), self, self.show_previous)
        QShortcut(QKeySequence("D"), self, self.show_next)
        QShortcut(QKeySequence("Ctrl+B"), self, self.add_bookmark)

        self._show_current_page()

    def _page_path(self, index):
        return os.path.join(self.image_dir, self.filenames[index])

    def _show_current_page(self):
        filename = self.filenames[self.current_index]
        pixmap = QPixmap(self._page_path(self.current_index))
        if pixmap.isNull():
            self.image_view.clear_source(f"Failed to load {filename}")
        else:
            self.image_view.set_source(pixmap)
        self.page_label.setText(
            f"{self.current_index + 1} / {len(self.filenames)}  —  {filename}"
        )
        self.thumb_strip.set_current(self.current_index)
        self._sync_list_selection()

    def _on_thumb_clicked(self, index):
        if index == self.current_index:
            return
        self.current_index = index
        self._show_current_page()

    def show_previous(self):
        if self.current_index > 0:
            self.current_index -= 1
            self._show_current_page()

    def show_next(self):
        if self.current_index < len(self.filenames) - 1:
            self.current_index += 1
            self._show_current_page()

    def _refresh_list(self, select_row=None):
        self.list_widget.blockSignals(True)
        self.list_widget.clear()
        for bookmark in self.bookmarks:
            item = QListWidgetItem(f"{bookmark['name']}  ·  {bookmark['page']}")
            item.setData(Qt.ItemDataRole.UserRole, bookmark["page"])
            self.list_widget.addItem(item)
        self.list_widget.blockSignals(False)
        if select_row is not None and 0 <= select_row < self.list_widget.count():
            self.list_widget.setCurrentRow(select_row)
        else:
            self._sync_list_selection()

    def _sync_list_selection(self):
        page = self.filenames[self.current_index]
        self.list_widget.blockSignals(True)
        matched = False
        for i, bookmark in enumerate(self.bookmarks):
            if bookmark["page"] == page:
                self.list_widget.setCurrentRow(i)
                matched = True
                break
        if not matched:
            self.list_widget.clearSelection()
        self.list_widget.blockSignals(False)

    def _on_bookmark_clicked(self, item):
        page = item.data(Qt.ItemDataRole.UserRole)
        if page in self.filenames:
            self.current_index = self.filenames.index(page)
            self._show_current_page()

    def _prompt_name(self, title, default):
        name, ok = QInputDialog.getText(self, title, "Bookmark name:", text=default)
        if not ok:
            return None
        name = name.strip()
        return name or None

    def add_bookmark(self):
        page = self.filenames[self.current_index]
        existing = next((i for i, b in enumerate(self.bookmarks) if b["page"] == page), None)
        if existing is not None:
            default = self.bookmarks[existing]["name"]
            name = self._prompt_name("Rename Bookmark", default)
            if name:
                self.bookmarks[existing]["name"] = name
                self._refresh_list(select_row=existing)
            return

        default = self.comic_name if not self.bookmarks else ""
        name = self._prompt_name("Add Bookmark", default)
        if name:
            self.bookmarks.append({"name": name, "page": page})
            self._refresh_list(select_row=len(self.bookmarks) - 1)

    def rename_bookmark(self):
        row = self.list_widget.currentRow()
        if row < 0:
            return
        name = self._prompt_name("Rename Bookmark", self.bookmarks[row]["name"])
        if name:
            self.bookmarks[row]["name"] = name
            self._refresh_list(select_row=row)

    def delete_bookmark(self):
        row = self.list_widget.currentRow()
        if row < 0:
            return
        del self.bookmarks[row]
        self._refresh_list()

    def _move_bookmark(self, delta):
        row = self.list_widget.currentRow()
        target = row + delta
        if row < 0 or target < 0 or target >= len(self.bookmarks):
            return
        self.bookmarks[row], self.bookmarks[target] = self.bookmarks[target], self.bookmarks[row]
        self._refresh_list(select_row=target)


def ensure_app():
    """Return a live QApplication. The instance must be kept referenced."""
    app = QApplication.instance()
    if app is None:
        app = QApplication(sys.argv)
    return app


def edit_bookmarks(image_dir, filenames, comic_name):
    """Open the bookmark editor and return the edited bookmark list."""
    # Keep a local reference so the QApplication is not garbage-collected
    # before QWidget construction (PyQt will abort otherwise).
    app = ensure_app()
    dialog = BookmarkEditorDialog(image_dir, filenames, comic_name)
    dialog.exec()
    return list(dialog.bookmarks)


def process_archive(zip_path, output_base_dir):
    """Process a single ZIP archive."""
    try:
        with zipfile.ZipFile(zip_path, 'r') as zf:
            namelist = zf.namelist()

            # 1. Locate meta.json
            meta_filename = next((f for f in namelist if os.path.basename(f) == "meta.json"), None)
            if not meta_filename:
                print(f"[SKIP] {os.path.basename(zip_path)}: 'meta.json' not found inside archive.")
                return

            # Read and parse meta.json directly in memory
            with zf.open(meta_filename) as f:
                try:
                    meta_data = json.loads(f.read().decode('utf-8'))
                except json.JSONDecodeError:
                    print(f"[SKIP] {os.path.basename(zip_path)}: 'meta.json' contains invalid JSON.")
                    return

            # 2. Extract metadata
            comic_id = str(meta_data.get("id"))
            if not comic_id or comic_id == "None":
                print(f"[SKIP] {os.path.basename(zip_path)}: Missing valid 'id' in meta.json.")
                return

            title_info = meta_data.get("title", {})
            # Prefer Japanese title, fall back to English, or default to "Unknown Title"
            comic_name = title_info.get("japanese") or title_info.get("english") or "Unknown Title"

            tags_list = meta_data.get("tags", [])

            # Extract artists for the 'author' field
            artists = [t["name"] for t in tags_list if t.get("type") == "artist"]
            author = ", ".join(artists) if artists else "anonymous"

            # Extract all other tags excluding artist
            tags = [t["name"] for t in tags_list if t.get("type") != "artist"]

            # 3. Locate and sort image files
            image_files = [f for f in namelist if f.lower().endswith(SUPPORTED_EXTENSIONS)]
            if not image_files:
                print(f"[SKIP] {os.path.basename(zip_path)}: No supported image files found.")
                return

            image_files.sort(key=natural_sort_key)

            # 4. Create target output directory (e.g., /path/to/output/646383)
            target_dir = os.path.join(output_base_dir, comic_id)
            os.makedirs(target_dir, exist_ok=True)

            # 5. Extract and rename images
            page_count = len(image_files)
            num_digits = len(str(page_count))
            new_filenames = []

            for i, img_path in enumerate(image_files, start=1):
                ext = os.path.splitext(img_path)[1].lower()
                new_name = f"{i:0{num_digits}d}{ext}"
                new_filenames.append(new_name)

                target_img_path = os.path.join(target_dir, new_name)

                # Stream file directly from ZIP archive to target path
                with zf.open(img_path) as source, open(target_img_path, "wb") as target:
                    shutil.copyfileobj(source, target)

            # 6. Let the user browse pages and edit bookmarks
            print(f"[EDIT] {os.path.basename(zip_path)}: opening bookmark editor ({page_count} pages)")
            bookmarks = edit_bookmarks(target_dir, new_filenames, comic_name)

            # 7. Assemble and save summary.json
            summary_data = {
                "comic_name": comic_name,
                "page_count": page_count,
                "bookmarks": bookmarks,
                "author": author,
                "tags": tags,
                "list": new_filenames
            }

            summary_path = os.path.join(target_dir, "summary.json")
            with open(summary_path, 'w', encoding='utf-8') as f:
                json.dump(summary_data, f, indent=2, ensure_ascii=False)

            print(
                f"[SUCCESS] {os.path.basename(zip_path)} -> {target_dir} "
                f"({page_count} pages, {len(bookmarks)} bookmark(s))"
            )

    except zipfile.BadZipFile:
        print(f"[ERROR] {os.path.basename(zip_path)}: Corrupted or invalid ZIP archive.")
    except Exception as e:
        print(f"[ERROR] Failed to process {os.path.basename(zip_path)}: {e}")


def main():
    parser = argparse.ArgumentParser(
        description="Batch restructure comic ZIP archives containing meta.json metadata."
    )
    parser.add_argument(
        "input_path",
        help="Source directory containing ZIP packages, or a single ZIP file (e.g., /path/to/packs or /path/to/one.zip)",
    )
    parser.add_argument("output_dir", help="Destination directory for processed comics (e.g., /path/to/output)")
    args = parser.parse_args()

    input_path = args.input_path
    output_dir = args.output_dir

    if not os.path.exists(input_path):
        print(f"Error: Provided input path '{input_path}' does not exist.")
        sys.exit(1)

    if os.path.isfile(input_path):
        if not input_path.lower().endswith('.zip'):
            print(f"Error: Provided input file '{input_path}' is not a .zip archive.")
            sys.exit(1)
        pack_files = [input_path]
    elif os.path.isdir(input_path):
        pack_files = [
            os.path.join(input_path, f)
            for f in os.listdir(input_path)
            if f.lower().endswith('.zip')
        ]
        if not pack_files:
            print(f"No .zip archives found in '{input_path}'.")
            return
    else:
        print(f"Error: Provided input path '{input_path}' is neither a file nor a directory.")
        sys.exit(1)

    # Ensure output directory exists
    os.makedirs(output_dir, exist_ok=True)

    # Hold a strong reference for the lifetime of main(); constructing a
    # QWidget after the QApplication is GC'd aborts with:
    # "QWidget: Must construct a QApplication before a QWidget"
    app = ensure_app()

    print(f"Found {len(pack_files)} archive(s). Starting process...\n" + "-" * 40)

    for zip_path in pack_files:
        process_archive(zip_path, output_dir)

    print("-" * 40 + "\nAll tasks completed successfully!")


if __name__ == "__main__":
    main()
