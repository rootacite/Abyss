import json
import os
import sys
import subprocess
import shutil
from pathlib import Path

def get_video_duration(video_path):
    """Get video duration in milliseconds using ffprobe"""
    try:
        cmd = [
            'ffprobe', 
            '-v', 'error',
            '-show_entries', 'format=duration',
            '-of', 'default=noprint_wrappers=1:nokey=1',
            str(video_path)
        ]
        result = subprocess.run(cmd, capture_output=True, text=True, check=True)
        duration_seconds = float(result.stdout.strip())
        return int(duration_seconds * 1000)
    except (subprocess.CalledProcessError, FileNotFoundError, ValueError) as e:
        print(f"Error getting video duration: {e}")
        return 0

def create_thumbnails(video_path, gallery_path, num_thumbnails=10):
    """
    Extracts thumbnails from a video and saves them to the gallery directory.
    """
    try:
        # Check if ffmpeg is installed
        subprocess.run(['ffmpeg', '-version'], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except (subprocess.CalledProcessError, FileNotFoundError):
        print("Error: ffmpeg is not installed or not in your PATH. Skipping thumbnail creation.")
        return

    try:
        # Get video duration using ffprobe
        duration_cmd = [
            'ffprobe', '-v', 'error', '-show_entries', 'format=duration',
            '-of', 'default=noprint_wrappers=1:nokey=1', str(video_path)
        ]
        result = subprocess.run(duration_cmd, capture_output=True, text=True, check=True)
        duration = float(result.stdout)
    except (subprocess.CalledProcessError, ValueError) as e:
        print(f"Could not get duration for '{video_path}': {e}. Skipping thumbnail creation.")
        return
    
    if duration <= 0:
        print(f"Warning: Invalid video duration for '{video_path}'. Skipping thumbnail creation.")
        return

    interval = duration / (num_thumbnails + 1)
    
    print(f"Generating {num_thumbnails} thumbnails for {video_path.name}...")
    
    for i in range(num_thumbnails):
        timestamp = (i + 1) * interval
        output_thumbnail_path = gallery_path / f"{i}.jpg"

        ffmpeg_cmd = [
            'ffmpeg', '-ss', str(timestamp), '-i', str(video_path),
            '-vframes', '1', '-q:v', '2', str(output_thumbnail_path), '-y'
        ]

        try:
            subprocess.run(ffmpeg_cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            print(f"  Extracted thumbnail {i}.jpg")
        except subprocess.CalledProcessError as e:
            print(f"  Error extracting thumbnail {i}.jpg: {e}")

def update_summary(base_path, name_input=None, author_input=None):
    """
    Updates the summary.json file for a given path.
    name_input and author_input are optional, used for the '-a' mode.
    """
    summary_path = base_path / "summary.json"
    video_path = base_path / "video.mp4"
    gallery_path = base_path / "gallery"

    # Default template
    default_summary = {
        "name": name_input if name_input is not None else "null",
        "duration": 0,
        "gallery": [],
        "comment": [],
        "star": False,
        "like": 0,
        "author": author_input if author_input is not None else "anonymous"
    }
    
    # Load existing summary if available
    if summary_path.exists():
        try:
            with open(summary_path, 'r', encoding='utf-8') as f:
                existing_data = json.load(f)
                # Update default with existing values
                for key in default_summary:
                    if key in existing_data:
                        default_summary[key] = existing_data[key]
        except json.JSONDecodeError:
            print("Warning: Invalid JSON in summary.json, using defaults")
    
    # Update duration from video file
    if video_path.exists():
        default_summary["duration"] = get_video_duration(video_path)
    else:
        print(f"Warning: video.mp4 not found at {video_path}")
    
    # Update gallery from directory
    if gallery_path.exists() and gallery_path.is_dir():
        gallery_files = []
        for file in gallery_path.iterdir():
            if file.is_file():
                gallery_files.append(file.name)
        gallery_files.sort()
        default_summary["gallery"] = gallery_files
    else:
        print(f"Warning: gallery directory not found at {gallery_path}")
    
    # Write updated summary
    with open(summary_path, 'w', encoding='utf-8') as f:
        json.dump(default_summary, f, indent=4, ensure_ascii=False)
    
    print(f"Summary updated successfully at {summary_path}")

def find_next_directory(base_path):
    """Find the next available integer directory name."""
    existing_dirs = set()
    for item in base_path.iterdir():
        if item.is_dir() and item.name.isdigit():
            existing_dirs.add(int(item.name))
    
    next_num = 1
    while next_num in existing_dirs:
        next_num += 1
    return str(next_num)

def main():
    if len(sys.argv) < 2:
        print("Usage: python script.py <command> [arguments]")
        print("Commands:")
        print("  -u <path>              Update the summary.json in the specified path.")
        print("  -a <video_file> <path> Add a new video project in a new directory under the specified path.")
        sys.exit(1)
        
    command = sys.argv[1]

    if command == '-u':
        if len(sys.argv) != 3:
            print("Usage: python script.py -u <path>")
            sys.exit(1)
        base_path = Path(sys.argv[2])
        if not base_path.is_dir():
            print(f"Error: Path not found or is not a directory: {base_path}")
            sys.exit(1)
        update_summary(base_path)

    elif command == '-a':
        if len(sys.argv) != 4:
            print("Usage: python script.py -a <video_file> <path>")
            sys.exit(1)
        
        video_source_path = Path(sys.argv[2])
        base_path = Path(sys.argv[3])

        if not video_source_path.exists() or not video_source_path.is_file():
            print(f"Error: Video file not found: {video_source_path}")
            sys.exit(1)

        if not base_path.is_dir():
            print(f"Error: Base path not found or is not a directory: {base_path}")
            sys.exit(1)
        
        # Find a new directory name (e.g., "1", "2", "3")
        new_dir_name = find_next_directory(base_path)
        new_project_path = base_path / new_dir_name
        
        # Create the new project directory and the gallery subdirectory
        new_project_path.mkdir(exist_ok=True)
        gallery_path = new_project_path / "gallery"
        gallery_path.mkdir(exist_ok=True)
        print(f"New project directory created at {new_project_path}")
        
        # Copy video file to the new directory
        shutil.copy(video_source_path, new_project_path / "video.mp4")
        print(f"Video copied to {new_project_path / 'video.mp4'}")

        # --- 新增功能：自动生成缩略图 ---
        video_dest_path = new_project_path / "video.mp4"
        create_thumbnails(video_dest_path, gallery_path)
        # ------------------------------------

        # Get user input for name and author
        video_name = input("Enter the video name: ")
        video_author = input("Enter the author's name: ")

        # Update the summary with user input
        update_summary(new_project_path, name_input=video_name, author_input=video_author)

    else:
        print("Invalid command. Use -u or -a.")
        print("Usage: python script.py <command> [arguments]")
        sys.exit(1)

if __name__ == "__main__":
    main()
