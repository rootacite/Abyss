import json
import os
import sys
import subprocess
import shutil
from pathlib import Path

ALLOWED_VIDEO_EXTS = [".mp4", ".mkv", ".webm", ".mov", ".ogg", ".ts", ".m2ts"]

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
        subprocess.run(['ffmpeg', '-version'], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except (subprocess.CalledProcessError, FileNotFoundError):
        print("Error: ffmpeg is not installed or not in your PATH. Skipping thumbnail creation.")
        return

    try:
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

def create_cover(video_path, output_path, time_percent):
    """
    Creates a cover image from a video at a specified time percentage.
    """
    try:
        subprocess.run(['ffmpeg', '-version'], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except (subprocess.CalledProcessError, FileNotFoundError):
        print("Error: ffmpeg is not installed or not in your PATH. Cannot create cover.")
        return

    try:
        duration_cmd = [
            'ffprobe', '-v', 'error', '-show_entries', 'format=duration',
            '-of', 'default=noprint_wrappers=1:nokey=1', str(video_path)
        ]
        result = subprocess.run(duration_cmd, capture_output=True, text=True, check=True)
        duration = float(result.stdout)
    except (subprocess.CalledProcessError, ValueError) as e:
        print(f"Could not get duration for '{video_path}': {e}. Cannot create cover.")
        return

    if duration <= 0:
        print(f"Warning: Invalid video duration for '{video_path}'. Cannot create cover.")
        return

    timestamp = duration * time_percent

    ffmpeg_cmd = [
        'ffmpeg', '-ss', str(timestamp), '-i', str(video_path),
        '-vframes', '1', str(output_path), '-y'
    ]

    print(f"Creating cover image from video at {timestamp:.2f} seconds...")
    try:
        subprocess.run(ffmpeg_cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print(f"Cover image created at {output_path}")
    except subprocess.CalledProcessError as e:
        print(f"Error creating cover image: {e}")

def find_video_in_dir(base_path):
    """
    Find video file in base_path. Preference:
      1) file named 'video' with allowed ext (video.mp4, video.mkv, ...)
      2) first file with allowed ext
    Returns Path or None.
    """
    if not base_path.exists() or not base_path.is_dir():
        return None

    # prefer explicit video.* name
    for ext in ALLOWED_VIDEO_EXTS:
        candidate = base_path / ("video" + ext)
        if candidate.exists() and candidate.is_file():
            return candidate

    # otherwise find first allowed extension file
    for f in base_path.iterdir():
        if f.is_file() and f.suffix.lower() in ALLOWED_VIDEO_EXTS:
            return f

    return None

def update_summary(base_path, name_input=None, author_input=None):
    """
    Updates the summary.json file for a given path.
    name_input and author_input are optional, used for the '-a' mode.
    """
    summary_path = base_path / "summary.json"
    gallery_path = base_path / "gallery"

    # Find the video file dynamically
    video_path = find_video_in_dir(base_path)
    if video_path is None:
        print(f"Warning: no video file found in {base_path}")

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

    # Update duration from video file if found
    if video_path and video_path.exists():
        default_summary["duration"] = get_video_duration(video_path)
    else:
        print("Warning: video file for duration not found; duration set to 0")

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
        print("  -a <video_file> <path> Add a new video project in a new directory under the specified path. Optional -y to accept defaults.")
        print("  -c <path> <time>       Create a cover image from the video in the specified path at a given time percentage (0.0-1.0).")
        sys.exit(1)

    command = sys.argv[1]

    # global -y flag (if present anywhere)
    assume_yes = '-y' in sys.argv

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
        # allow invocation with optional -y flag anywhere; expecting at least video and base path
        params = [p for p in sys.argv[2:] if p != '-y']
        if len(params) != 2:
            print("Usage: python script.py -a <video_file> <path>  (optional -y to accept defaults)")
            sys.exit(1)

        video_source_path = Path(params[0])
        base_path = Path(params[1])

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

        # Copy video file to the new directory, preserving extension in the target name
        dest_video_name = "video" + video_source_path.suffix.lower()
        video_dest_path = new_project_path / dest_video_name
        shutil.copy(video_source_path, video_dest_path)
        print(f"Video copied to {video_dest_path}")

        # Auto-generate thumbnails
        create_thumbnails(video_dest_path, gallery_path)

        # Auto-generate cover at 50%
        cover_path = new_project_path / "cover.jpg"
        create_cover(video_dest_path, cover_path, 0.5)

        # Get user input for name and author, unless assume_yes is set
        if assume_yes:
            video_name = video_source_path.stem
            video_author = "Anonymous"
            print(f"Assume yes (-y): using defaults: name='{video_name}', author='{video_author}'")
        else:
            print("\nEnter the video name (press Enter to use the original filename):")
            video_name = input(f"Video Name [{video_source_path.stem}]: ")
            if not video_name:
                video_name = video_source_path.stem

            print("\nEnter the author's name (press Enter to use 'Anonymous'):")
            video_author = input("Author Name [Anonymous]: ")
            if not video_author:
                video_author = "Anonymous"

        # Update the summary with user input or default values
        update_summary(new_project_path, name_input=video_name, author_input=video_author)

    elif command == '-c':
        if len(sys.argv) != 4:
            print("Usage: python script.py -c <path> <time>")
            sys.exit(1)

        base_path = Path(sys.argv[2])
        # find video dynamically
        video_path = find_video_in_dir(base_path)
        if video_path is None:
            print(f"Error: no video file found in {base_path}")
            sys.exit(1)
        cover_path = base_path / "cover.jpg"

        try:
            time_percent = float(sys.argv[3])
            if not 0.0 <= time_percent <= 1.0:
                raise ValueError
        except ValueError:
            print("Error: Time value must be a number between 0.0 and 1.0.")
            sys.exit(1)

        if not video_path.exists() or not video_path.is_file():
            print(f"Error: video file not found at {video_path}")
            sys.exit(1)

        create_cover(video_path, cover_path, time_percent)

    else:
        print("Invalid command. Use -u, -a, or -c.")
        print("Usage: python script.py <command> [arguments]")
        sys.exit(1)

if __name__ == "__main__":
    main()
