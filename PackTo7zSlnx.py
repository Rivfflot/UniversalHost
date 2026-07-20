import os
import subprocess

# ==============================
# 用户配置
# ==============================

# 只排除完全同名的目录
EXCLUDE_DIRS = [
    ".git",
    ".vs",
    ".vscode",
    "bin",
    "obj"
]

# 只排除完全同名的文件
EXCLUDE_FILES = [
    "PackTo7zSlnx.py",
]

#要移动到的路径
OUTPUT_DIR = r"C:\Users\cmr56\OneDrive\code\csharp"


# ==============================
# 7z程序路径
# ==============================

# Windows默认路径
SEVEN_ZIP = r"C:\Program Files\7-Zip\7z.exe"

# Linux/macOS:
# SEVEN_ZIP = "7zz"


# ==============================
# 生成7z排除参数
# ==============================

def build_exclude_args(exclude_dirs, exclude_files):

    args = []

    for dirname in exclude_dirs:
        args.append(
            f"-xr!{dirname}\\"
        )

    for filename in exclude_files:
        args.append(
            f"-x!{filename}"
        )

    return args


# ==============================
# 创建7z压缩包
# ==============================

def create_7z(source_dir, output_file):

    source_dir = os.path.abspath(source_dir)
    output_file = os.path.abspath(output_file)


    cmd = [
        SEVEN_ZIP,
        "a",              # 添加文件
        "-t7z",           # 7z格式
        "-mx=9",          # 最高压缩等级
        "-m0=lzma2",      # LZMA2算法
        "-ms=on",         # 固实压缩
        output_file,
        "."
    ]


    # 添加排除规则
    cmd.extend(
        build_exclude_args(
            EXCLUDE_DIRS,
            EXCLUDE_FILES
        )
    )


    print("执行命令:")
    print(" ".join(cmd))


    subprocess.run(
        cmd,
        cwd=source_dir,
        check=True
    )


# ==============================
# 主程序
# ==============================

if __name__ == "__main__":

    try:

        current_dir = os.getcwd()

        folder_name = os.path.basename(current_dir)

        output = os.path.join(OUTPUT_DIR,folder_name + ".7z")
        # output = os.path.join(
        #     current_dir,
        #     folder_name + ".7z"
        # )


        # 删除旧压缩包
        if os.path.exists(output):

            print("检测到已有压缩包，正在删除:")
            print(output)

            os.remove(output)


        print("\n开始压缩:")
        print(current_dir)


        create_7z(
            current_dir,
            output
        )


        print("\n压缩完成:")
        print(output)


    except Exception as e:

        print("\n程序发生错误:")
        print(e)


    finally:

        input("\n按任意键退出...")