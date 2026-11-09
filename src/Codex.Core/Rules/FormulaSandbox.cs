using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Codex.Core.Rules;

/// <summary>
/// What a parsed formula may contain (R7: formulas come from content packs anyone can import). The interpreter knows no
/// types, so <c>new</c>, <c>typeof</c> and static members do not parse. But DynamicExpresso lets a formula call public instance
/// methods on whatever it holds, and <c>'a'.Replace('a', 'aa')</c> chained forty times asks for a terabyte. So every parsed
/// formula is walked once, before it is cached:
/// <list type="bullet">
/// <item>calls: only the registered built-in functions (invoked as delegates), lambdas passed to them, and the string
/// concatenation <c>+</c> compiles to;</item>
/// <item>dynamic operations: reading a member (<c>c.classLevel</c>), operators and conversions. Not calling a member
/// (<c>c.name.Replace(...)</c>), setting one, or indexing;</item>
/// <item>no static or instance member reads on CLR types (<c>x.Length</c> on a string), no <c>new</c>, no arrays, no
/// assignment.</item>
/// </list>
/// Anything else is a <see cref="FormulaException"/>, which the sheet reports as the GM's formula problem.
/// </summary>
public static class FormulaSandbox
{
    // string.Concat is what `+` on strings compiles to, and Object.ToString what it calls on a value that is not one ('bab.' +
    // c.bab). Neither can grow a string beyond the sum of its parts, unlike Replace, PadLeft or Join called in a chain.
    private static readonly HashSet<MethodInfo> AllowedMethods = typeof(string).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(m => m.Name == nameof(string.Concat))
        .Append(typeof(object).GetMethod(nameof(ToString))!)
        .ToHashSet();

    public static void Check(Expression expression) => new Visitor().Visit(expression);

    private sealed class Visitor : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (!AllowedMethods.Contains(node.Method)) throw Refused($"calls {node.Method.DeclaringType?.Name}.{node.Method.Name}");
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitInvocation(InvocationExpression node)
        {
            switch (node.Expression)
            {
                // A built-in function: a delegate constant. (A lambda parameter is invoked only inside the built-ins.)
                case ConstantExpression { Value: Delegate }:
                    return base.VisitInvocation(node);

                // A dynamic operation, as DynamicExpresso builds it: site.Target(site, arguments). The site's binder says what it does.
                case MemberExpression { Member.Name: "Target", Expression: ConstantExpression { Value: CallSite site } }:
                    CheckBinder(site.Binder);
                    foreach (var argument in node.Arguments.Skip(1)) Visit(argument);
                    return node;

                default:
                    throw Refused("invokes something that is not a function");
            }
        }

        // DynamicExpresso's own binders (internal): reading a member is allowed; calling a method or indexing is not.
        private const string LateGetMember = "DynamicExpresso.Resolution.LateGetMemberCallSiteBinder";

        private static void CheckBinder(CallSiteBinder binder)
        {
            if (binder is GetMemberBinder or BinaryOperationBinder or UnaryOperationBinder or ConvertBinder) return;
            if (binder.GetType().FullName == LateGetMember) return;
            throw Refused(binder is InvokeMemberBinder invoke ? $"calls {invoke.Name}"
                : binder.GetType().Name.Contains("Invoke", StringComparison.Ordinal) ? "calls a method" : "uses an operation formulas do not have");
        }

        protected override Expression VisitDynamic(DynamicExpression node)
        {
            CheckBinder(node.Binder);
            return base.VisitDynamic(node);
        }

        protected override Expression VisitMember(MemberExpression node) => throw Refused($"reads {node.Member.DeclaringType?.Name}.{node.Member.Name}");
        protected override Expression VisitNew(NewExpression node) => throw Refused("creates an object");
        protected override Expression VisitNewArray(NewArrayExpression node) => throw Refused("creates an array");
        protected override Expression VisitIndex(IndexExpression node) => throw Refused("indexes");
        protected override Expression VisitMemberInit(MemberInitExpression node) => throw Refused("creates an object");
        protected override Expression VisitListInit(ListInitExpression node) => throw Refused("creates a list");

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType is ExpressionType.Assign or ExpressionType.ArrayIndex || node.NodeType.ToString().EndsWith("Assign", StringComparison.Ordinal))
            {
                throw Refused("assigns");
            }

            return base.VisitBinary(node);
        }

        private static FormulaException Refused(string what) => new($"a formula may not do that: it {what}");
    }
}
