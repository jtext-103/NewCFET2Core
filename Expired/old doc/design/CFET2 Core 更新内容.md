# CFET2 Core 更新内容

1. 增加了对.net framework和.net core 双版本的支持，使用CFET框架的类库都更改成了.net standard
2. 改进动态加载功能，详细可见CFET动态加载说明文档
3. 动态加载中添加了配置文件路径，这样就可以在动态配置文件中直接写入文件名称
4. 在Hub中提供了简单的get，set，invoke方法，不支持复杂类型参数
5. 修复了nancy的部分问题
原项目中无法正确识别访问来源是否为远程访问，已修正
明确了编码类型，目前支持messagePack和json