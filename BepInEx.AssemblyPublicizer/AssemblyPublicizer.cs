using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.Rows.FieldAttributes;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.Rows.MethodAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.Rows.TypeAttributes;

namespace BepInEx.AssemblyPublicizer;

public static class AssemblyPublicizer
{
    public static void Publicize(string assemblyPath, string outputPath, AssemblyPublicizerOptions? options = null)
    {
        var assembly = FatalAsmResolver.FromFile(assemblyPath);
        var maskAssembly = options?.MaskAssembly != null ? FatalAsmResolver.FromFile(options.MaskAssembly) : null; 
        var module = assembly.ManifestModule ?? throw new NullReferenceException();
        module.MetadataResolver = new DefaultMetadataResolver(NoopAssemblyResolver.Instance);

        Publicize(assembly, maskAssembly, options);
        module.FatalWrite(outputPath);
    }

    public static AssemblyDefinition Publicize(AssemblyDefinition assembly, AssemblyDefinition? maskAssembly, AssemblyPublicizerOptions? options = null)
    {
        options ??= new AssemblyPublicizerOptions();

        var module = assembly.ManifestModule!;
        var maskModule = maskAssembly?.ManifestModule;

        var attribute = options.IncludeOriginalAttributesAttribute ? new OriginalAttributesAttribute(module) : null;

        var maskTypes = maskModule?.GetAllTypes().ToDictionary(x => x.FullName);
        
        foreach (var typeDefinition in module.GetAllTypes())
        {
            if (attribute != null && typeDefinition == attribute.Type)
                continue;
            if (maskTypes != null && !maskTypes.ContainsKey(typeDefinition.FullName))
                continue;

            Publicize(typeDefinition, maskTypes?[typeDefinition.FullName], attribute, options);
        }

        return assembly;
    }

    private static void Publicize(TypeDefinition typeDefinition, TypeDefinition? maskTypeDefinition, OriginalAttributesAttribute? attribute, AssemblyPublicizerOptions options)
    {
        if (options.Strip && !typeDefinition.IsEnum && !typeDefinition.IsInterface)
        {
            foreach (var methodDefinition in typeDefinition.Methods)
            {
                if (!methodDefinition.HasMethodBody)
                    continue;

                var newBody = methodDefinition.CilMethodBody = new CilMethodBody(methodDefinition);
                newBody.Instructions.Add(CilOpCodes.Ldnull);
                newBody.Instructions.Add(CilOpCodes.Throw);
                methodDefinition.NoInlining = true;
            }
        }

        if (!options.PublicizeCompilerGenerated && typeDefinition.IsCompilerGenerated())
            return;

        if (options.HasTarget(PublicizeTarget.Types) && (!typeDefinition.IsNested && !typeDefinition.IsPublic || typeDefinition.IsNested && !typeDefinition.IsNestedPublic))
        {
            if (attribute != null)
                typeDefinition.CustomAttributes.Add(attribute.ToCustomAttribute(typeDefinition.Attributes & TypeAttributes.VisibilityMask));

            typeDefinition.Attributes &= ~TypeAttributes.VisibilityMask;
            typeDefinition.Attributes |= typeDefinition.IsNested ? TypeAttributes.NestedPublic : TypeAttributes.Public;
        }

        if (options.HasTarget(PublicizeTarget.Methods))
        {
            var maskMethods = maskTypeDefinition?.Methods.Select(x => x.FullName).ToArray();
            
            foreach (var methodDefinition in typeDefinition.Methods)
            {
                if (maskMethods != null && !maskMethods.Contains(methodDefinition.FullName))
                    continue;
                
                Publicize(methodDefinition, attribute, options);
            }

            // Special case for accessors generated from auto properties, publicize them regardless of PublicizeCompilerGenerated
            if (!options.PublicizeCompilerGenerated)
            {
                foreach (var propertyDefinition in typeDefinition.Properties)
                {
                    if (propertyDefinition.GetMethod is { } getMethod) Publicize(getMethod, attribute, options, true);
                    if (propertyDefinition.SetMethod is { } setMethod) Publicize(setMethod, attribute, options, true);
                }
            }
        }

        if (options.HasTarget(PublicizeTarget.Fields))
        {
            var maskFields = maskTypeDefinition?.Fields.Select(x => x.FullName).ToArray();
            
            var eventNames = new HashSet<Utf8String?>(typeDefinition.Events.Select(e => e.Name));
            foreach (var fieldDefinition in typeDefinition.Fields)
            {
                if (fieldDefinition.IsPrivateScope)
                    continue;
                if (maskFields != null && !maskFields.Contains(fieldDefinition.FullName))
                    continue;

                if (!fieldDefinition.IsPublic)
                {
                    // Skip event backing fields
                    if (eventNames.Contains(fieldDefinition.Name))
                        continue;

                    if (!options.PublicizeCompilerGenerated && fieldDefinition.IsCompilerGenerated())
                        continue;

                    if (attribute != null)
                        fieldDefinition.CustomAttributes.Add(attribute.ToCustomAttribute(fieldDefinition.Attributes & FieldAttributes.FieldAccessMask));

                    fieldDefinition.Attributes &= ~FieldAttributes.FieldAccessMask;
                    fieldDefinition.Attributes |= FieldAttributes.Public;
                }
            }
        }
    }

    private static void Publicize(MethodDefinition methodDefinition, OriginalAttributesAttribute? attribute, AssemblyPublicizerOptions options, bool ignoreCompilerGeneratedCheck = false)
    {
        if (methodDefinition.IsCompilerControlled)
            return;

        if (!methodDefinition.IsPublic)
        {
            if (!ignoreCompilerGeneratedCheck && !options.PublicizeCompilerGenerated && methodDefinition.IsCompilerGenerated())
                return;

            if (attribute != null)
                methodDefinition.CustomAttributes.Add(attribute.ToCustomAttribute(methodDefinition.Attributes & MethodAttributes.MemberAccessMask));

            methodDefinition.Attributes &= ~MethodAttributes.MemberAccessMask;
            methodDefinition.Attributes |= MethodAttributes.Public;
        }
    }
}
