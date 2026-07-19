using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Linq.Dynamic.Core.CustomTypeProviders;
using System.Reflection;
using System.Runtime.CompilerServices;
using ExileCore.Shared.Enums;

namespace FollowHer.Core.Combat.Rules;

/// <summary>Compiles a rule's condition text (a single boolean expression against
/// SkillRuleContext) via Dynamic LINQ - the same "v1" mechanism ReAgent uses for its rules,
/// scoped down to a single expression since a skill-priority condition never needs multi-statement
/// scripting.</summary>
public static class RuleExpressionCompiler
{
    private static readonly ParsingConfig ParsingConfig = new()
    {
        AllowNewToEvaluateAnyType = true,
        ResolveTypesBySimpleName = true,
        CustomTypeProvider = new SkillRuleTypeProvider(),
    };

    public static (Func<SkillRuleContext, bool> Func, string Error) Compile(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return (_ => false, null);
        }

        try
        {
            var lambda = DynamicExpressionParser.ParseLambda<SkillRuleContext, bool>(ParsingConfig, false, expression);
            return (lambda.Compile(), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private sealed class SkillRuleTypeProvider :
        AbstractDynamicLinqCustomTypeProvider,
        IDynamicLinkCustomTypeProvider,
        IDynamicLinqCustomTypeProvider
    {
        private HashSet<Type> _cachedCustomTypes;
        private Dictionary<Type, List<MethodInfo>> _cachedExtensionMethods;

        public HashSet<Type> GetCustomTypes() => _cachedCustomTypes ??= new HashSet<Type>(
            FindTypesMarkedWithDynamicLinqTypeAttribute(AppDomain.CurrentDomain.GetAssemblies())
                .Concat(typeof(SkillRuleTypeProvider).Assembly.GetExportedTypes())
                .Append(typeof(MonsterRarity)));

        public Dictionary<Type, List<MethodInfo>> GetExtensionMethods() => _cachedExtensionMethods ??= GetCustomTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(x => x.IsDefined(typeof(ExtensionAttribute), false)))
            .GroupBy(x => x.GetParameters()[0].ParameterType)
            .ToDictionary(key => key.Key, methods => methods.ToList());

        public Type ResolveType(string typeName) => ResolveType(AppDomain.CurrentDomain.GetAssemblies(), typeName);

        public Type ResolveTypeBySimpleName(string simpleTypeName) =>
            ResolveTypeBySimpleName(AppDomain.CurrentDomain.GetAssemblies(), simpleTypeName);
    }
}
