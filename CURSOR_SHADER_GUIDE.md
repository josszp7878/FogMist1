# 在Cursor中使用Shader文件指南

## 已知问题
OmniSharp可能会尝试处理Shader文件并报错：
```
[Error] OmniSharp.Extensions.JsonRpc.InputHandler: Failed to handle request textDocument/didOpen - System.NotSupportedException: The language 'SHADERLAB' is not supported.
```

这是因为OmniSharp主要为C#设计，而非Shader语言。

## 使用推荐

### 1. 使用ShaderFunctionMap.md作为快速参考
已为您创建了`Assets/VolumetricFog2/ShaderFunctionMap.md`文件，其中列出了所有关键Shader函数及其位置。使用此文件快速跳转到需要的函数。

### 2. 使用搜索功能
Cursor的搜索功能非常强大：
- 使用 `Ctrl+Shift+F` 或 `Cmd+Shift+F` 全局搜索函数名
- 搜索时使用精确匹配语法，如：`ComputeFog(float3 wpos, float2 uv)`

### 3. 安装推荐的扩展
请安装以下扩展以获得更好的Shader编辑体验：
- `ms-vscode.cpptools`
- `slevesque.shader`
- `asd555555.shaderlablanguage`

## 安装扩展的步骤
1. 在Cursor中，点击左侧的扩展图标（或使用快捷键`Ctrl+Shift+X`/`Cmd+Shift+X`）
2. 搜索所需扩展名
3. 点击安装按钮
4. 安装完成后重启Cursor

## 配置已完成
本项目已进行了以下配置来优化Shader体验：
- 文件关联：将.cginc、.hlsl、.shader文件关联到相应的语言模式
- HLSL预处理器定义：添加了常用的Unity宏
- 包含目录设置：已添加Shader文件目录
- OmniSharp配置：排除了Shader文件，避免C#语言服务器处理它们

## 故障排除
如果上述步骤后问题仍然存在：
1. 使用创建的ShaderFunctionMap.md作为主要导航工具
2. 考虑在Unity中直接编辑Shader文件
3. 或使用专门的Shader编辑器，如ShaderLab VS Code

## 备选方案
您也可以在Unity编辑器中直接编辑Shader：
1. 在Project窗口中找到Shader文件
2. 双击打开内置编辑器
3. 编辑完成后保存，Unity会自动重新编译Shader 