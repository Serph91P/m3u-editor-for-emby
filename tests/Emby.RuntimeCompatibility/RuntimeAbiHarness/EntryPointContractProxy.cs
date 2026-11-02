using System;
using System.Reflection;

namespace Emby.RuntimeCompatibility
{
    public class EntryPointContractProxy : DispatchProxy
    {
        internal int Adds;
        internal int Removes;
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name.StartsWith("add_", StringComparison.Ordinal)) Adds++;
            if (method.Name.StartsWith("remove_", StringComparison.Ordinal)) Removes++;
            return method.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }
}
