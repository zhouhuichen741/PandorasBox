using ECommons.DalamudServices;
using PandorasBox.FeaturesSetup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PandorasBox.Features.Commands
{
    public class XlMute : CommandFeature
    {
        public override string Name => "自定义屏蔽词";
        public override string Command { get; set; } = "/pmute";
        public override string[] Alias => Array.Empty<string>();
        public override string Description => "向 Dalamud 内置的屏蔽词列表添加/移除词语。\n添加: /pmute <词语>\n删除: /punmute <词语>\n屏蔽词列表: /pmutelist";
        public override bool ShowInHelp => false;

        public override List<string> Parameters => new() { "[<词语>]" };

        public override bool UseAutoConfig => false;

        private readonly List<string> extraRegisteredCommands = new();

        public override void Setup()
        {
            base.Setup();
        }

        protected override void OnCommandInternal(string _, string args)
        {
            OnCommand(args.Split(' ').ToList());
        }

        public override void Enable()
        {
            base.Enable();

            Svc.Commands.AddHandler("/pmutelist", new Dalamud.Game.Command.CommandInfo(OnListCommand)
            { HelpMessage = "[Pandora's Box 自定义屏蔽词]", ShowInHelp = false });
            extraRegisteredCommands.Add("/pmutelist");

            Svc.Commands.AddHandler("/punmute", new Dalamud.Game.Command.CommandInfo(OnUnmuteCommand)
            { HelpMessage = "[Pandora's Box 自定义屏蔽词]", ShowInHelp = false });
            extraRegisteredCommands.Add("/punmute");
        }

        public override void Disable()
        {
            foreach (var c in extraRegisteredCommands)
                Svc.Commands.RemoveHandler(c);
            extraRegisteredCommands.Clear();
            base.Disable();
        }

        protected override void OnCommand(List<string> args)
        {
            var arguments = string.Join(" ", args);

            if (string.IsNullOrEmpty(arguments))
            {
                Svc.Chat.Print("请提供要屏蔽的词语。用法: /pmute <词语>");
                return;
            }

            var configInstance = GetDalamudConfig(out var configType);
            if (configInstance == null) return;

            try
            {
                var badWordsProp = configType.GetProperty("BadWords");
                var badWords = badWordsProp.GetValue(configInstance) as IList<string>;
                if (badWords == null)
                {
                    badWords = new List<string>();
                    badWordsProp.SetValue(configInstance, badWords);
                }

                if (badWords.Contains(arguments))
                {
                    Svc.Chat.Print("该屏蔽词已存在于屏蔽词列表中。");
                    return;
                }

                badWords.Add(arguments);

                var saveMethod = configType.GetMethod("QueueSave");
                saveMethod?.Invoke(configInstance, null);

                Svc.Chat.Print("添加屏蔽词成功。");
            }
            catch (Exception ex)
            {
                Svc.Chat.PrintError("添加屏蔽词失败: " + ex.Message);
            }
        }

        private void OnListCommand(string command, string args)
        {
            var configInstance = GetDalamudConfig(out var configType);
            if (configInstance == null) return;

            try
            {
                var badWordsProp = configType.GetProperty("BadWords");
                var badWords = badWordsProp.GetValue(configInstance) as IList<string>;

                if (badWords == null || badWords.Count == 0)
                {
                    Svc.Chat.Print("当前没有屏蔽词。");
                    return;
                }

                Svc.Chat.Print($"屏蔽词列表 ({badWords.Count}) 已输出到日志窗口，请按 /xllog 查看。");

                for (var i = 0; i < badWords.Count; i++)
                {
                    Svc.Log.Information($"  [{i + 1}] \"{badWords[i]}\"");
                }
            }
            catch (Exception ex)
            {
                Svc.Chat.PrintError("获取屏蔽词列表失败: " + ex.Message);
            }
        }

        private void OnUnmuteCommand(string command, string args)
        {
            var arguments = args?.Trim();

            if (string.IsNullOrEmpty(arguments))
            {
                Svc.Chat.Print("请提供要取消屏蔽的词语。用法: /punmute <词语>");
                return;
            }

            var configInstance = GetDalamudConfig(out var configType);
            if (configInstance == null) return;

            try
            {
                var badWordsProp = configType.GetProperty("BadWords");
                var badWords = badWordsProp.GetValue(configInstance) as IList<string>;

                if (badWords == null || badWords.Count == 0)
                {
                    Svc.Chat.Print("屏蔽词列表为空。");
                    return;
                }

                var badWordsList = badWords as List<string>;
                if (badWordsList == null)
                {
                    badWordsList = badWords.ToList();
                    badWordsProp.SetValue(configInstance, badWordsList);
                }

                var removedCount = badWordsList.RemoveAll(x => x == arguments);

                if (removedCount == 0)
                {
                    Svc.Chat.Print($"\"{arguments}\" 不在屏蔽词列表中。");
                    return;
                }

                var saveMethod = configType.GetMethod("QueueSave");
                saveMethod?.Invoke(configInstance, null);

                Svc.Chat.Print(removedCount == 1 
                    ? $"已取消屏蔽 \"{arguments}\"。" 
                    : $"已取消屏蔽 \"{arguments}\"，共删除 {removedCount} 个重复项。");
            }
            catch (Exception ex)
            {
                Svc.Chat.PrintError("取消屏蔽词失败: " + ex.Message);
            }
        }

        private object? GetDalamudConfig(out Type? configType)
        {
            configType = null;
            try
            {
                var dalamudAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Dalamud");

                if (dalamudAssembly == null)
                {
                    Svc.Chat.PrintError("找不到 Dalamud 程序集。");
                    return null;
                }

                configType = dalamudAssembly.GetTypes()
                    .FirstOrDefault(t => t.Name == "DalamudConfiguration");

                if (configType == null)
                {
                    Svc.Chat.PrintError("找不到 DalamudConfiguration 类型。");
                    return null;
                }

                var serviceTypeDef = dalamudAssembly.GetTypes()
                    .FirstOrDefault(t => t.IsGenericType && t.Name == "Service`1");

                if (serviceTypeDef == null)
                {
                    Svc.Chat.PrintError("找不到 Service<T> 类型。");
                    return null;
                }

                var constructedType = serviceTypeDef.MakeGenericType(configType);
                var getMethod = constructedType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static);

                if (getMethod == null)
                {
                    Svc.Chat.PrintError("找不到 Service<DalamudConfiguration>.Get() 方法。");
                    return null;
                }

                var instance = getMethod.Invoke(null, null);
                if (instance == null)
                    Svc.Chat.PrintError("无法获取 DalamudConfiguration 实例。");

                return instance;
            }
            catch (Exception ex)
            {
                Svc.Chat.PrintError("无法访问 Dalamud 内部配置: " + ex.Message);
                return null;
            }
        }
    }
}