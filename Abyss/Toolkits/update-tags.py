import os
import json
import sys

def process_directory(base_path):
    # 检查基础路径是否存在
    if not os.path.exists(base_path):
        print(f"错误：路径 '{base_path}' 不存在")
        return
    
    # 遍历基础路径下的所有子目录
    for item in os.listdir(base_path):
        item_path = os.path.join(base_path, item)
        
        # 只处理目录，忽略文件
        if os.path.isdir(item_path):
            summary_path = os.path.join(item_path, "summary.json")
            
            # 检查summary.json文件是否存在
            if os.path.exists(summary_path):
                try:
                    # 读取JSON文件
                    with open(summary_path, 'r', encoding='utf-8') as f:
                        data = json.load(f)
                    
                    # 获取comic_name和tags
                    comic_name = data.get('comic_name', '未知名称')
                    tags = data.get('tags', [])
                    
                    # 输出信息
                    print(f"\n漫画名称: {comic_name}")
                    print(f"当前标签: {tags}")
                    
                    # 提示用户输入新标签
                    user_input = input("请输入新标签（多个标签用英文逗号分隔，直接回车跳过）: ").strip()
                    
                    if user_input:
                        # 分割用户输入的标签
                        new_tags = [tag.strip() for tag in user_input.split(',') if tag.strip()]
                        
                        if new_tags:
                            # 添加新标签到列表
                            tags.extend(new_tags)
                            data['tags'] = tags
                            
                            # 写回文件
                            with open(summary_path, 'w', encoding='utf-8') as f:
                                json.dump(data, f, ensure_ascii=False, indent=4)
                            
                            print(f"已添加新标签: {new_tags}")
                        else:
                            print("未输入有效标签，跳过")
                    else:
                        print("未输入标签，跳过")
                        
                except json.JSONDecodeError:
                    print(f"错误：{summary_path} 不是有效的JSON文件")
                except Exception as e:
                    print(f"处理文件 {summary_path} 时出错: {e}")
            else:
                print(f"跳过目录 {item}：未找到summary.json文件")

def main():
    if len(sys.argv) != 2:
        print("用法: python script.py <目录路径>")
        sys.exit(1)
    
    base_dir = sys.argv[1]
    process_directory(base_dir)

if __name__ == "__main__":
    main()
