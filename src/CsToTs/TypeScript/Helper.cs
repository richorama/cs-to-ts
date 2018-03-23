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
 
            var template = options != null && !string.IsNullOrEmpty(options.Template)
                ? options.Template
                : Template;

            Handlebars.Configuration.TextEncoder = SimpleEncoder.Instance;

            var generator = Handlebars.Compile(template);
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

            var declaration = GetTypeName(type, context);
            if (type.BaseType != typeof(object)) {
                if (type.IsInterface || context.Options.UseInterfaceForClasses) {
                    interfaces.Insert(0, type.BaseType);
                }
                else if (PopulateTypeDefinition(type.BaseType, context) != null) {
                    declaration += $" extends {GetTypeRef(type.BaseType, context)}";
                }
            }

            interfaces = interfaces.Where(i => PopulateTypeDefinition(i, context) != null).ToList();
            
            var typeDef = new TypeDefinition(type);
            context.Types.Add(typeDef);

            var interfacesStr = interfaces.Any() 
                ? $" implements {string.Join(", ", interfaces.Select(i => GetTypeRef(i, context)))}"
                : string.Empty;
            typeDef.Declaration = $"export class {declaration}{interfacesStr}";
            
            typeDef.Members.AddRange(GetMembers(type, context));
            return typeDef;
        }

        private static void PopulateEnumDefinition(Type type, TypeScriptContext context) {
            var existing = context.Enums.FirstOrDefault(t => t.ClrType == type);
            if (existing != null) return;

            var names = Enum.GetNames(type);
            var members = new List<EnumField>();
            foreach (var name in names) {
                var value = Convert.ToInt32(Enum.Parse(type, name));
                members.Add(new EnumField(name, value.ToString()));
            }

            var def = new EnumDefinition(type.Name, members);
            context.Enums.Add(def);
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
            if (!type.IsGenericType) return type.Name;

            var typeName = StripGenericFromName(type);

            var genericPrms = type.GetGenericArguments().Select(g => {
                var constraints = g.GetGenericParameterConstraints()
                    .Where(c => PopulateTypeDefinition(c, context) != null)
                    .Select(c => GetTypeRef(c, context))
                    .ToList();

                if (g.GenericParameterAttributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint)) {
                    constraints.Add($"{{ new() => {g.Name}}}");
                }

                if (constraints.Any()) {
                    typeName = $"{typeName} extends {string.Join(" & ", constraints)}";
                }

                return typeName;
            });

            return string.Join(", ", genericPrms);
        }

        private static string GetTypeRef(Type type, TypeScriptContext context) {
            if (type.IsGenericParameter)
                return type.Name;
            
            if (type.IsEnum)
                return context.Enums.Any(e => e.ClrType == type) ? type.Name : "any";

            var typeCode = Type.GetTypeCode(type);
            if (typeCode != TypeCode.Object) 
                return GetPrimitiveMemberType(typeCode, context.Options);
            
            if (typeof(IEnumerable).IsAssignableFrom(type))
                return $"Array<{GetTypeRef(type.GetGenericArguments().First(), context)}>";
                
            var typeDef = PopulateTypeDefinition(type, context);
            if (typeDef == null) 
                return "any";

            var typeName = StripGenericFromName(type);
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
        
        private static string StripGenericFromName(Type type) => type.Name.Substring(0, type.Name.IndexOf('`'));
 
        private static string GetDefaultTemplate() {
            var ass = typeof(Generator).Assembly;
            var resourceName = ass.GetManifestResourceNames().First(r => r.Contains("template.handlebars"));
            using (var reader = new StreamReader(ass.GetManifestResourceStream(resourceName), Encoding.UTF8)) {
                return reader.ReadToEnd();
            }
        }
    }
}