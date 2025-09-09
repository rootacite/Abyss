import json
import os
import sys
from pathlib import Path

def process_directory(directory_path):
    """
    处理指定目录，扫描图片文件并更新summary.json
    
    Args:
        directory_path (str): 目录路径
    """
    try:
        # 转换为Path对象
        path = Path(directory_path)
        
        # 检查目录是否存在
        if not path.exists() or not path.is_dir():
            print(f"错误: 目录 '{directory_path}' 不存在或不是目录")
            return False
        
        # 支持的图片文件扩展名
        image_extensions = {'.jpg', '.jpeg', '.png', '.gif', '.bmp', '.tiff', '.webp'}
        
        # 扫描目录中的图片文件
        image_files = []
        for file in path.iterdir():
            if file.is_file() and file.suffix.lower() in image_extensions:
                image_files.append(file.name)
        
        # 按文件名排序
        image_files.sort()
        
        print(f"找到 {len(image_files)} 个图片文件")
        
        # 读取或创建summary.json
        summary_file = path / "summary.json"
        if summary_file.exists():
            try:
                with open(summary_file, 'r', encoding='utf-8') as f:
                    summary_data = json.load(f)
            except json.JSONDecodeError:
                print("错误: summary.json 格式不正确")
                return False
        else:
            summary_data = {}
        
        # 更新列表
        summary_data['list'] = image_files
        
        # 写回文件
        with open(summary_file, 'w', encoding='utf-8') as f:
            json.dump(summary_data, f, ensure_ascii=False, indent=2)
        
        print(f"成功更新 {summary_file}")
        return True
        
    except Exception as e:
        print(f"处理过程中发生错误: {e}")
        return False

def main():
    # 检查命令行参数
    if len(sys.argv) != 2:
        print("用法: python script.py <目录路径>")
        sys.exit(1)
    
    directory_path = sys.argv[1]
    
    # 处理目录
    if process_directory(directory_path):
        print("操作完成")
    else:
        print("操作失败")
        sys.exit(1)

if __name__ == "__main__":
    main()
