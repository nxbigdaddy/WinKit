using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using WinKit.Core.Diagnostics;

namespace WinKit.App.Services
{
    /// <summary>
    /// 插件注册表：用反射发现当前程序集里所有 IToolModule 实现。
    /// 采用自动发现而不是显式注册表，好处是各功能页互不依赖、可并行开发。
    /// </summary>
    public static class ModuleRegistry
    {
        public static IReadOnlyList<IToolModule> Discover()
        {
            var modules = new List<IToolModule>();
            var assembly = Assembly.GetExecutingAssembly();

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
                Log.Warn("部分类型加载失败，插件发现可能不完整：" + ex.Message);
            }

            foreach (var type in types)
            {
                if (type == null) continue;
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IToolModule).IsAssignableFrom(type)) continue;

                try
                {
                    if (Activator.CreateInstance(type) is IToolModule module)
                    {
                        modules.Add(module);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("插件实例化失败：" + type.FullName + " → " + ex.Message);
                }
            }

            return modules
                .OrderBy(m => m.Group, StringComparer.CurrentCulture)
                .ThenBy(m => m.Order)
                .ThenBy(m => m.Title, StringComparer.CurrentCulture)
                .ToList();
        }
    }
}
