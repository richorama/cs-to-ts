﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HandlebarsDotNet;

namespace CsToTs.TypeScript {

    public static class Helper {
        private static readonly Lazy<string> _lazyTemplate = new Lazy<string>(GetDefaultTemplate);
        private static string Template => _lazyTemplate.Value;

        private static bool SkipCheck(string s, ReflectOptions o) =>
            s != null && o.SkipTypePatterns.Any(p => Regex.Match(s, p).Success);

        internal static string GenerateTypeScript(IEnumerable<Type> types, TypeScriptOptions options) {
            var context = new TypeScriptContext(options);
            GetTypeScriptDefinitions(types, context);
 
            Handlebars.Configuration.TextEncoder = SimpleEncoder.Instance;

            var generator = Handlebars.Compile(Template);
            return generator(context);
        }

        private static void GetTypeScriptDefinitions(IEnumerable<Type> types, TypeScriptContext context) {
            foreach (var type in types) {
                if (!type.IsEnum) {
                    PopulateTypeDefinition(type, context);
                }
                else {
                    PopulateEnumDefinition(type, context);
                }
            }
        }

        private static TypeDefinition PopulateTypeDefinition(Type type, TypeScriptContext context) {
            if (SkipCheck(type.ToString(), context.Options)) return null;

            if (type.IsConstructedGenericType) {
                type = type.GetGenericTypeDefinition();
            }

            var existing = context.Types.FirstOrDefault(t => t.ClrType == type);
            if (existing != null) return existing;

            var interfaces = type.GetInterfaces().ToList();
            var interfaceRefs = interfaces
                .Except(type.BaseType?.GetInterfaces() ?? Enumerable.Empty<Type>())
                .Except(interfaces.SelectMany(i => i.GetInterfaces()))
                .Where(i => PopulateTypeDefinition(i, context) != null)
                .Select(i => GetTypeRef(i, context))
                .ToList();

            var isInterface = type.IsInterface || context.Options.UseInterfaceForClasses;
            var baseTypeRef = string.Empty;
            if (type.IsClass) {
                if (type.BaseType != typeof(object) && PopulateTypeDefinition(type.BaseType, context) != null) {
                    baseTypeRef = GetTypeRef(type.BaseType, context);
                }
                else if (context.Options.DefaultBaseType != null) {
                    baseTypeRef = context.Options.DefaultBaseType(type);
                }
            }

            var declaration = GetTypeName(type, context);
            if (isInterface) {
                declaration = $"export interface {declaration}";

                if (!string.IsNullOrEmpty(baseTypeRef)) {
                    interfaceRefs.Insert(0, baseTypeRef);
                }
            }
            else {
                var abs = type.IsAbstract ? " abstract" : string.Empty;
                declaration = $"export{abs} class {declaration}";

                if (!string.IsNullOrEmpty(baseTypeRef)) {
                    declaration = $"{declaration} extends {baseTypeRef}";
                }
            }
            
            if (interfaceRefs.Any()) {
                var imp = isInterface ? "extends" : "implements";
                var interfaceRefStr = string.Join(", ", interfaceRefs);
                declaration = $"{declaration} {imp} {interfaceRefStr}";
            }

            var typeDef = new TypeDefinition(type, declaration);
            context.Types.Add(typeDef);
            typeDef.Members.AddRange(GetMembers(type, context));

            return typeDef;
        }

        private static EnumDefinition PopulateEnumDefinition(Type type, TypeScriptContext context) {
            var existing = context.Enums.FirstOrDefault(t => t.ClrType == type);
            if (existing != null) return existing;

            var members = Enum.GetNames(type)
                .Select(n => new EnumField(n, Convert.ToInt32(Enum.Parse(type, n)).ToString()));

            var def = new EnumDefinition(type, ApplyRename(type.Name, context.Options), members);
            context.Enums.Add(def);
            
            return def;
        }
        
        private static IEnumerable<MemberDefinition> GetMembers(Type type, TypeScriptContext context) {
            const BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            var memberDefs = type.GetFields(bindingFlags)
                .Select(f => new MemberDefinition(f.Name, GetTypeRef(f.FieldType, context)))
                .ToList();

            memberDefs.AddRange(
                type.GetProperties(bindingFlags)
                    .Select(p => new MemberDefinition(p.Name, GetTypeRef(p.PropertyType, context)))
            );
            
            return memberDefs;
        }

        private static string GetTypeName(Type type, TypeScriptContext context) {
            if (!type.IsGenericType) return ApplyRename(type.Name, context.Options);

            var genericPrms = type.GetGenericArguments().Select(g => {
                var constraints = g.GetGenericParameterConstraints()
                    .Where(c => PopulateTypeDefinition(c, context) != null)
                    .Select(c => GetTypeRef(c, context))
                    .ToList();

                if (g.IsClass
                    && !context.Options.UseInterfaceForClasses 
                    && g.GenericParameterAttributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint)) {
                    constraints.Add($"{{ new(): {g.Name} }}");
                }

                if (constraints.Any()) {
                    return $"{g.Name} extends {string.Join(" & ", constraints)}";
                }

                return g.Name;
            });

            return $"{ApplyRename(StripGenericFromName(type), context.Options)}<{string.Join(", ", genericPrms)}>";
        }

        private static string GetTypeRef(Type type, TypeScriptContext context) {
            if (type.IsGenericParameter)
                return ApplyRename(type.Name, context.Options);

            if (type.IsEnum) {
                var enumDef = PopulateEnumDefinition(type, context);
                return enumDef != null ? ApplyRename(type.Name, context.Options) : "any";
            }

            var typeCode = Type.GetTypeCode(type);
            if (typeCode != TypeCode.Object) 
                return GetPrimitiveMemberType(typeCode, context.Options);
            
            if (typeof(IEnumerable).IsAssignableFrom(type))
                return $"Array<{GetTypeRef(type.GetGenericArguments().First(), context)}>";
                
            var typeDef = PopulateTypeDefinition(type, context);
            if (typeDef == null) 
                return "any";

            var typeName = ApplyRename(StripGenericFromName(type), context.Options);
            if (type.IsGenericType) {
                var genericPrms = type.GetGenericArguments().Select(t => GetTypeRef(t, context));
                return $"{typeName}<{string.Join(", ", genericPrms)}>";
            }

            return typeName;
        }
        
        private static string GetPrimitiveMemberType(TypeCode typeCode, TypeScriptOptions options) {
            switch (typeCode) {
                case TypeCode.Boolean:
                    return "boolean";
                case TypeCode.Byte:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.SByte:
                case TypeCode.Single:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                    return "number";
                case TypeCode.Char:
                case TypeCode.String:
                    return "string";
                case TypeCode.DateTime:
                    return options.UseDateForDateTime ? "Date" : "string";
                default:
                    return "any";
            }
        }
        
        private static string StripGenericFromName(Type type) 
            => type.IsGenericType ? type.Name.Substring(0, type.Name.IndexOf('`')) : type.Name;

        private static string ApplyRename(string typeName, TypeScriptOptions options)
            => options.TypeRenamer != null ? options.TypeRenamer(typeName) : typeName;
 
        private static string GetDefaultTemplate() {
            var ass = typeof(Generator).Assembly;
            var resourceName = ass.GetManifestResourceNames().First(r => r.Contains("template.handlebars"));
            using (var reader = new StreamReader(ass.GetManifestResourceStream(resourceName), Encoding.UTF8)) {
                return reader.ReadToEnd();
            }
        }
    }
}