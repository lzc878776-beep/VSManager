// 可选的动作实现示例：复制到自己的适配包目录并改名为 actions.cs 后生效（C# 5 语法，编译进 CAD 引导 DLL）。
// Optional action implementation sample: copy it into your adapter folder and rename it to actions.cs (C# 5 syntax, compiled into the CAD boot DLL).
//
// 约定 / Contract:
//   namespace VSManagerCadBoot.Custom { public sealed class Actions : ICadActionOverride { ... } }
//   返回 true 表示已处理该动作（可新增动作名或覆盖内置动作）；返回 false 交给内置实现。
//   Return true when the action is handled (adds a new action name or overrides a built-in one); return false to use the built-in implementation.
//   访问 CAD API 必须通过 context.OnMainThread(...) 在主线程执行。
//   CAD API calls must run on the main thread through context.OnMainThread(...).
using System;
using System.Collections.Generic;
using VSManager.CadAgent;

namespace VSManagerCadBoot.Custom
{
    public sealed class Actions : ICadActionOverride
    {
        public bool TryExecute(CadActionRequest request, CadActionContext context, out CadActionResult result)
        {
            result = null;
            // 示例：getParam 的 name=plugin.version 返回插件自定义参数。
            // Example: getParam with name=plugin.version returns a plug-in specific value.
            if (request.Action == CadActions.GetParam && request.Arg("name") == "plugin.version")
            {
                string version = context.OnMainThread(delegate { return "1.0.0"; }, request.EffectiveTimeoutMs);
                result = CadActionResult.Success("plugin.version = " + version);
                return true;
            }
            return false;
        }
    }
}
