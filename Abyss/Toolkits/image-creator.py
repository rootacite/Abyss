import os
import sys
import json
import re
import tkinter as tk
from tkinter import simpledialog, Toplevel, Canvas, Frame, Scrollbar
from PIL import Image, ImageTk

# --- Configuration ---
# Supported image file extensions
SUPPORTED_EXTENSIONS = ('.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp')
# Default thumbnail size for the GUI on first launch
DEFAULT_THUMBNAIL_SIZE = (300, 300)
# Number of columns in the GUI grid
GRID_COLUMNS = 5


def natural_sort_key(s):
    return [int(text) if text.isdigit() else text.lower()
            for text in re.split('([0-9]+)', s)]


class BookmarkApp:
    """
    A GUI application for selecting images and creating bookmarks, with zoom functionality.
    """

    def __init__(self, parent, image_dir, image_files):
        """
        Initialize the bookmark creation window.
        """
        self.top = Toplevel(parent)
        self.top.title("Bookmark Creator | Keys: [+] Zoom In, [-] Zoom Out")

        self.top.grid_rowconfigure(0, weight=1)
        self.top.grid_columnconfigure(0, weight=1)

        self.image_dir = image_dir
        self.image_files = image_files
        self.bookmarks = []
        self._photo_images = []  # To prevent garbage collection

        # --- Zoom Configuration ---
        self.current_size = DEFAULT_THUMBNAIL_SIZE[0]
        self.zoom_step = 25
        self.min_zoom_size = 50
        self.max_zoom_size = 500

        # --- Create a scrollable frame ---
        self.canvas = Canvas(self.top)
        self.scrollbar = Scrollbar(self.top, orient="vertical", command=self.canvas.yview)
        self.scrollable_frame = Frame(self.canvas)

        self.scrollable_frame.bind(
            "<Configure>",
            lambda e: self.canvas.configure(
                scrollregion=self.canvas.bbox("all")
            )
        )

        self.canvas.create_window((0, 0), window=self.scrollable_frame, anchor="nw")
        self.canvas.configure(yscrollcommand=self.scrollbar.set)

        self.canvas.grid(row=0, column=0, sticky="nsew")
        self.scrollbar.grid(row=0, column=1, sticky="ns")

        # --- Bind Events ---
        self.top.bind('<MouseWheel>', self._on_mousewheel)
        self.top.bind('<Button-4>', self._on_mousewheel)
        self.top.bind('<Button-5>', self._on_mousewheel)
        # Bind zoom keys
        self.top.bind('<KeyPress-plus>', self._zoom_in)
        self.top.bind('<KeyPress-equal>', self._zoom_in)  # For keyboards where + is shift+=
        self.top.bind('<KeyPress-minus>', self._zoom_out)

        self._repopulate_images()

    def _zoom_in(self, event=None):
        """Increases the size of the thumbnails."""
        new_size = self.current_size + self.zoom_step
        if new_size > self.max_zoom_size:
            new_size = self.max_zoom_size

        if new_size != self.current_size:
            self.current_size = new_size
            print(f"Zoom In. New thumbnail size: {self.current_size}x{self.current_size}")
            self._repopulate_images()

    def _zoom_out(self, event=None):
        """Decreases the size of the thumbnails."""
        new_size = self.current_size - self.zoom_step
        if new_size < self.min_zoom_size:
            new_size = self.min_zoom_size

        if new_size != self.current_size:
            self.current_size = new_size
            print(f"Zoom Out. New thumbnail size: {self.current_size}x{self.current_size}")
            self._repopulate_images()

    def _on_mousewheel(self, event):
        """Handle mouse wheel scrolling."""
        if sys.platform == "linux":
            scroll_delta = -1 if event.num == 4 else 1
        else:
            scroll_delta = int(-1 * (event.delta / 120))
        self.canvas.yview_scroll(scroll_delta, "units")

    def _repopulate_images(self):
        """
        Clear and redraw all images in the grid with the current size.
        This is called on initial load and after every zoom action.
        """
        # Clear existing widgets
        for widget in self.scrollable_frame.winfo_children():
            widget.destroy()
        self._photo_images.clear()  # Clear the photo references

        new_thumbnail_size = (self.current_size, self.current_size)

        for i, filename in enumerate(self.image_files):
            try:
                filepath = os.path.join(self.image_dir, filename)
                with Image.open(filepath) as img:
                    img.thumbnail(new_thumbnail_size, Image.Resampling.LANCZOS)
                    photo = ImageTk.PhotoImage(img)
                    self._photo_images.append(photo)

                    container = Frame(self.scrollable_frame, bd=2, relief="groove")
                    img_label = tk.Label(container, image=photo)
                    img_label.pack()

                    text_label = tk.Label(container, text=filename)
                    text_label.pack()

                    container.bind("<Button-1>", lambda e, f=filename: self.add_bookmark(f))
                    img_label.bind("<Button-1>", lambda e, f=filename: self.add_bookmark(f))
                    text_label.bind("<Button-1>", lambda e, f=filename: self.add_bookmark(f))

                    row = i // GRID_COLUMNS
                    col = i % GRID_COLUMNS
                    container.grid(row=row, column=col, padx=5, pady=5, sticky="nsew")
            except Exception as e:
                print(f"Warning: Could not load image {filename}. Error: {e}")

    def add_bookmark(self, page_filename):
        """Prompt user for a bookmark name and add it."""
        bookmark_name = simpledialog.askstring(
            "Add Bookmark",
            f"Enter a name for the bookmark on page:\n{page_filename}",
            parent=self.top
        )
        if bookmark_name:
            self.bookmarks.append({
                "name": bookmark_name,
                "page": page_filename
            })
            print(f"Success: Bookmark '{bookmark_name}' created for page '{page_filename}'.")

    def wait(self):
        """Wait for the Toplevel window to be closed."""
        self.top.wait_window()
        return self.bookmarks


def load_existing_summary(summary_path):
    """Load existing summary.json if it exists, else return None."""
    if os.path.exists(summary_path):
        try:
            with open(summary_path, 'r', encoding='utf-8') as f:
                return json.load(f)
        except (IOError, json.JSONDecodeError) as e:
            print(f"Warning: Could not read existing summary.json: {e}")
    return None


def get_tags_from_user():
    """Prompt user to enter tags in the console."""
    try:
        tags_input = input("Enter tags (comma-separated): ").strip()
        if tags_input:
            return [tag.strip() for tag in tags_input.split(",") if tag.strip()]
        return []
    except (KeyboardInterrupt, EOFError):
        print("\nOperation cancelled by user.")
        sys.exit(0)


def main():
    """
    Main function to execute the script.
    """
    # --- 1. Get and Validate Directory from Command Line ---
    if len(sys.argv) != 2:
        print("Usage: python restructure_comic.py <directory_path>")
        sys.exit(1)

    target_dir = sys.argv[1]
    if not os.path.isdir(target_dir):
        print(f"Error: The provided path '{target_dir}' is not a valid directory.")
        sys.exit(1)

    print(f"Processing directory: {target_dir}")

    # --- 2. Check for existing summary.json ---
    json_filepath = os.path.join(target_dir, "summary.json")
    existing_summary = load_existing_summary(json_filepath)

    # --- 3. Get User Input for Metadata ---
    if existing_summary:
        print("Found existing summary.json. Using existing data where available.")
        comic_name = existing_summary.get("comic_name", "")
        author = existing_summary.get("author", "anonymous")
        tags = existing_summary.get("tags", [])
        existing_bookmarks = existing_summary.get("bookmarks", [])

        # Only prompt for missing fields
        if not comic_name:
            try:
                comic_name = input("Enter the comic name: ")
            except (KeyboardInterrupt, EOFError):
                print("\nOperation cancelled by user.")
                sys.exit(0)
    else:
        try:
            comic_name = input("Enter the comic name: ")
            author = input("Enter the author name (or leave blank for 'anonymous'): ")
            if not author:
                author = "anonymous"
            tags = get_tags_from_user()
            existing_bookmarks = []
        except (KeyboardInterrupt, EOFError):
            print("\nOperation cancelled by user.")
            sys.exit(0)

    # --- 4. Scan, Sort, and Rename Image Files ---
    try:
        all_files = os.listdir(target_dir)
        image_files = sorted(
            [f for f in all_files if f.lower().endswith(SUPPORTED_EXTENSIONS)],
            key=natural_sort_key
        )

        if not image_files:
            print("Error: No supported image files found in the directory.")
            sys.exit(1)

        # Only rename files if we don't have an existing summary with a file list
        if existing_summary and "list" in existing_summary:
            new_filenames = existing_summary["list"]
            print("Using existing file list from summary.json")
        else:
            page_count = len(image_files)
            num_digits = len(str(page_count))
            new_filenames = []

            print("\nRenaming files...")
            for i, old_filename in enumerate(image_files, start=1):
                file_ext = os.path.splitext(old_filename)[1]
                new_filename_base = f"{i:0{num_digits}d}"
                new_filename = f"{new_filename_base}{file_ext}"

                old_filepath = os.path.join(target_dir, old_filename)
                new_filepath = os.path.join(target_dir, new_filename)

                if old_filepath != new_filepath:
                    os.rename(old_filepath, new_filepath)
                    print(f"  '{old_filename}' -> '{new_filename}'")
                else:
                    print(f"  '{old_filename}' is already correctly named. Skipping.")

                new_filenames.append(new_filename)

    except OSError as e:
        print(f"\nAn error occurred during file operations: {e}")
        sys.exit(1)

    print("\nFile operations complete.")

    # --- 5. Launch GUI for Bookmark Creation ---
    print("Launching bookmark creator GUI...")
    print("Please click on images in the new window to create bookmarks.")
    print("Use '+' and '-' keys to zoom in and out. Close the window when finished.")

    root = tk.Tk()
    root.withdraw()

    gui = BookmarkApp(root, target_dir, new_filenames)
    new_bookmarks = gui.wait()

    root.destroy()
    print("Bookmark creation finished.")

    # Combine existing bookmarks with new ones
    all_bookmarks = existing_bookmarks + new_bookmarks

    # --- 6. Create and Write summary.json ---
    summary_data = {
        "comic_name": comic_name,
        "page_count": len(new_filenames),
        "bookmarks": all_bookmarks,
        "author": author,
        "tags": tags,
        "list": new_filenames
    }

    try:
        with open(json_filepath, 'w', encoding='utf-8') as f:
            json.dump(summary_data, f, indent=2, ensure_ascii=False)
        print(f"\nSuccessfully created/updated '{json_filepath}'")
    except IOError as e:
        print(f"\nError: Could not write to '{json_filepath}'. Reason: {e}")
        sys.exit(1)

    print("\nOperation completed successfully!")


if __name__ == "__main__":
    main()