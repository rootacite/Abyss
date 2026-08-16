import os
import sys
import re
import json
import zipfile
import shutil
import argparse

# Supported image file extensions
SUPPORTED_EXTENSIONS = ('.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp')


def natural_sort_key(s):
    """Natural sort algorithm to ensure 2.webp comes before 10.webp."""
    basename = os.path.basename(s)
    return [int(text) if text.isdigit() else text.lower()
            for text in re.split('([0-9]+)', basename)]


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

            # 6. Generate silent bookmark (first page only, named after the comic)
            bookmarks = [{
                "name": comic_name,
                "page": new_filenames[0]
            }]

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

            print(f"[SUCCESS] {os.path.basename(zip_path)} -> {target_dir} ({page_count} pages)")

    except zipfile.BadZipFile:
        print(f"[ERROR] {os.path.basename(zip_path)}: Corrupted or invalid ZIP archive.")
    except Exception as e:
        print(f"[ERROR] Failed to process {os.path.basename(zip_path)}: {e}")


def main():
    parser = argparse.ArgumentParser(
        description="Batch restructure comic ZIP archives containing meta.json metadata."
    )
    parser.add_argument("input_dir", help="Source directory containing ZIP packages (e.g., /path/to/packs)")
    parser.add_argument("output_dir", help="Destination directory for processed comics (e.g., /path/to/output)")
    args = parser.parse_args()

    input_dir = args.input_dir
    output_dir = args.output_dir

    if not os.path.isdir(input_dir):
        print(f"Error: Provided input path '{input_dir}' is not a valid directory.")
        sys.exit(1)

    # Ensure output directory exists
    os.makedirs(output_dir, exist_ok=True)

    # Scan for ZIP archives
    pack_files = [f for f in os.listdir(input_dir) if f.lower().endswith('.zip')]
    
    if not pack_files:
        print(f"No .zip archives found in '{input_dir}'.")
        return

    print(f"Found {len(pack_files)} archive(s). Starting process...\n" + "-" * 40)

    for filename in pack_files:
        zip_path = os.path.join(input_dir, filename)
        process_archive(zip_path, output_dir)
        
    print("-" * 40 + "\nAll tasks completed successfully!")


if __name__ == "__main__":
    main()
