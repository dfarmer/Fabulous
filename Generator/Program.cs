﻿// dotnet build Generator\Generator.csproj && dotnet  Generator\bin\Debug\netcoreapp2.0\Generator.dll Generator\bindings.json Elmish.XamarinForms\DynamicXaml.fs && fsc -a -r:packages\androidapp\Xamarin.Forms\lib\netstandard1.0\Xamarin.Forms.Core.dll Elmish.XamarinForms\DynamicXamlConverters.fs Elmish.XamarinForms\DynamicXaml.fs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Newtonsoft.Json;

namespace Generator
{
    class Bindings
    {
        // Input
        public List<string> Assemblies { get; set; }
        public List<TypeBinding> Types { get; set; }
        public string OutputNamespace { get; set; }

        // Output
        public List<AssemblyDefinition> AssemblyDefinitions { get; set; }

        public TypeDefinition GetTypeDefinition(string name) =>
            (from a in AssemblyDefinitions
             from m in a.Modules
             from tdef in m.Types
             where tdef.FullName == name
             select tdef).First();

        public TypeBinding FindType (string name) => Types.FirstOrDefault (x => x.Name == name);
    }

    class TypeBinding
    {
        // Input
        public string Name { get; set; }
        public string ModelName { get; set; }
        public string CustomType { get; set; }
        public List<MemberBinding> Members { get; set; }

        // Output
        public string BoundCode { get; set; }
        public TypeDefinition Definition { get; set; }

        public string BoundName => "XamlElement"; // Definition.Name + "Description";
    }

    class MemberBinding
    {
        // Input
        public string Name { get; set; }
        public string UniqueName { get; set; }
        public string ShortName { get; set; }
        public string Default { get; set; }
        public string Equality { get; set; }
        public string Conv { get; set; }
        public string Apply { get; set; }
        public string ApplyCode { get; set; }
        public string ModelType { get; set; }
        public string ElementType { get; set; }
        public string InputType { get; set; }
        public List<MemberBinding> Attached { get; set; }

        // Output
        public MemberReference Definition { get; set; }

        public TypeReference BoundType =>
            (Definition is PropertyDefinition p) 
              ? p.PropertyType 
              : ((EventDefinition)Definition).EventType;

        public string BoundUniqueName => string.IsNullOrEmpty(UniqueName) ? Name : UniqueName;
        public string LowerBoundUniqueName => char.ToLowerInvariant (BoundUniqueName[0]) + BoundUniqueName.Substring (1);
        public string BoundShortName => string.IsNullOrEmpty(ShortName) ? Name : ShortName;
        public string LowerBoundShortName => char.ToLowerInvariant(BoundShortName[0]) + BoundShortName.Substring(1);
        public string GetInputType(Bindings bindings, IEnumerable<Tuple<TypeReference, TypeDefinition>> hierarchy)
        {
            if (!string.IsNullOrWhiteSpace(InputType))
            {
                return InputType;
            }
            return this.GetModelType(bindings, hierarchy);
        }
        public string GetModelType(Bindings bindings, IEnumerable<Tuple<TypeReference, TypeDefinition>> hierarchy)
        {
            if (!string.IsNullOrWhiteSpace(ModelType))
            {
                return ModelType;
            }
            return GetModelTypeInner(bindings, this.BoundType, hierarchy);
        }
        public static string GetModelTypeInner(Bindings bindings, TypeReference tref, IEnumerable<Tuple<TypeReference, TypeDefinition>> hierarchy)
        {
            if (tref.IsGenericParameter)
            {
                if (hierarchy != null)
                {
                    var r = Program.ResolveGenericParameter(tref, hierarchy);
                    return GetModelTypeInner(bindings, r, hierarchy);
                }
                else
                {
                    return "XamlElement";
                }
            }
            if (tref.IsGenericInstance)
            {
                var n = tref.Name.Substring(0, tref.Name.IndexOf('`'));
                var ns = tref.Namespace;
                if (tref.IsNested)
                {
                    n = tref.DeclaringType.Name + "." + n;
                    ns = tref.DeclaringType.Namespace;
                }
                var args = string.Join(", ", ((GenericInstanceType)tref).GenericArguments.Select(s => GetModelTypeInner(bindings, s, hierarchy)));
                return $"{ns}.{n}<{args}>";
            }
            switch (tref.FullName)
            {
                case "System.String": return "string";
                case "System.Boolean": return "bool";
                case "System.Int32": return "int";
                case "System.Double": return "double";
                case "System.Single": return "single";
                default:
                    if (bindings.Types.FirstOrDefault(x => x.Name == tref.FullName) is TypeBinding tb)
                        return tb.BoundName;
                    return tref.FullName.Replace('/', '.');
            }
        }
        public string GetElementType(IEnumerable<Tuple<TypeReference, TypeDefinition>> hierarchy)
        {
            if (!string.IsNullOrWhiteSpace(ElementType))
            {
                return ElementType;
            }
            return GetElementTypeInner(this.BoundType, hierarchy);
        }
        static string GetElementTypeInner(TypeReference tref, IEnumerable<Tuple<TypeReference, TypeDefinition>> hierarchy)
        {
            var r = Program.ResolveGenericParameter(tref, hierarchy);
            if (r == null)
                return null;
            if (r.FullName == "System.String")
                return null;
            if (r.Name == "IList`1" && r.IsGenericInstance)
            {
                var args = ((GenericInstanceType)r).GenericArguments;
                var elementType = Program.ResolveGenericParameter(args[0], hierarchy);
                return elementType.Name;
            }
            else
            {
                var bs = r.Resolve().Interfaces;
                return bs.Select(b => GetElementTypeInner(b.InterfaceType, hierarchy)).FirstOrDefault(b => b != null);
            }
        }

    }

    class Program
    {
        static int Main(string[] args)
        {
            try {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("usage: generator <outputPath>");
                    Environment.Exit(1);
                }
                var bindingsPath = args[0];
                var outputPath = args[1];

                var bindings = JsonConvert.DeserializeObject<Bindings> (File.ReadAllText (bindingsPath));

                bindings.AssemblyDefinitions = bindings.Assemblies.Select(LoadAssembly).ToList();

                foreach (var x in bindings.Types)
                    x.Definition = bindings.GetTypeDefinition (x.Name);
                foreach (var x in bindings.Types) {
                    foreach (var m in x.Members) {
                        if (FindProperty (m.Name, x.Definition) is PropertyDefinition p) {
                            m.Definition = p;
                        }
                        else if (FindEvent (m.Name, x.Definition) is EventDefinition e) {
                            m.Definition = e;
                        }
                        else {
                            throw new Exception ($"Could not find member `{m.Name}`");
                        }
                    }
                }
                var code = BindTypes (bindings);

                File.WriteAllText (outputPath, code);
                return 0;
            }
            catch (Exception ex) {
                System.Console.WriteLine(ex);
                return 1;
            }
        }

        static string BindTypes (Bindings bindings)
        {
            var w = new StringWriter();
            var head = "";

            w.WriteLine("namespace rec " + bindings.OutputNamespace);
            w.WriteLine();
            w.WriteLine("#nowarn \"67\" // cast always holds");
            w.WriteLine();

            w.WriteLine($"    /// Produce a new visual element with an adjusted attribute");
            w.WriteLine($"[<AutoOpen>]");
            w.WriteLine($"module XamlElementExtensions = ");
            w.WriteLine();
            w.WriteLine($"    type XamlElement with");
            foreach (var type in bindings.Types)
            {
                var tdef = type.Definition;
                var hierarchy = GetHierarchy(type.Definition).ToList();
                var ctor = tdef.Methods
                    .Where(x => x.IsConstructor && x.IsPublic)
                    .OrderBy(x => x.Parameters.Count)
                    .FirstOrDefault();

                w.WriteLine();
                if (string.IsNullOrWhiteSpace(type.ModelName))
                {
                    w.WriteLine($"        /// Create a {tdef.FullName} from the view description");
                    w.WriteLine($"        member x.CreateAs{tdef.Name}() : {tdef.FullName} = (x.Create() :?> {tdef.FullName})");
                }
            }
            var allMembersInAllTypes = new List<MemberBinding>();
            foreach (var type in bindings.Types)
            {
                if (type.Members != null)
                {
                    foreach (var y in type.Members)
                    {
                        allMembersInAllTypes.Add(y);
                        if (y.Attached != null)
                        {
                            foreach (var ap in y.Attached)
                                allMembersInAllTypes.Add(ap);
                        }
                    }
                }
            }
            var allMembersInAllTypesGroupedByName = allMembersInAllTypes.GroupBy(y => y.BoundUniqueName);
            /*            foreach (var ms in allMembersInAllTypesGroupedByName)
                        {
                            var m = ms.First();
                            w.WriteLine();
                            w.WriteLine($"        /// Get the {m.BoundUniqueName} property in the visual element");
                            w.WriteLine("        member x." + m.BoundUniqueName + " = match x.Attributes.TryFind(\"" + m.BoundUniqueName + "\") with Some v -> unbox<" + GetModelType(bindings, m.BoundType, null) + ">(v) | None -> " + m.Default);
                        }
                        */
            foreach (var ms in allMembersInAllTypesGroupedByName)
            {
                var m = ms.First();
                w.WriteLine();
                w.WriteLine($"        /// Try to get the {m.BoundUniqueName} property in the visual element");
                var modelType = m.GetModelType(bindings, null);
                w.WriteLine("        member x.Try" + m.BoundUniqueName + " = match x.Attributes.TryFind(\"" + m.BoundUniqueName + "\") with Some v -> Some(unbox<" + modelType + ">(v)) | None -> None");
            }
            foreach (var ms in allMembersInAllTypesGroupedByName)
            {
                var m = ms.First();
                w.WriteLine();
                w.WriteLine($"        /// Adjusts the {m.BoundUniqueName} property in the visual element");
                var conv = string.IsNullOrWhiteSpace(m.Conv) ? "" : m.Conv;
                var inputType = m.GetInputType(bindings, null);
                w.WriteLine("        member x." + m.BoundUniqueName + "(value: " + inputType + ") = XamlElement(x.TargetType, x.CreateMethod, x.ApplyMethod, x.Attributes.Add(\"" + m.BoundUniqueName + "\", box (" + conv + "(value))))");
            }
            w.WriteLine();
            foreach (var ms in allMembersInAllTypesGroupedByName)
            {
                var m = ms.First();
                var inputType = m.GetInputType(bindings, null);
                w.WriteLine();
                w.WriteLine($"    /// Adjusts the {m.BoundUniqueName} property in the visual element");
                w.WriteLine("    let with" + m.BoundUniqueName + " (value: " + inputType + ") (x: XamlElement) = x." + m.BoundUniqueName + "(value)");
                w.WriteLine();
                w.WriteLine($"    /// Adjusts the {m.BoundUniqueName} property in the visual element");
                w.WriteLine("    let " + m.LowerBoundUniqueName + " (value: " + inputType + ") (x: XamlElement) = x." + m.BoundUniqueName + "(value)");
            }
            w.WriteLine();
            w.WriteLine("type Xaml() =");
            foreach (var type in bindings.Types)
            {
                var tdef = type.Definition;
                var nameOfCreator = string.IsNullOrWhiteSpace(type.ModelName) ? tdef.Name : type.ModelName;
                var customTypeToCreate = string.IsNullOrWhiteSpace(type.CustomType) ? tdef.FullName : type.CustomType;
                var hierarchy = GetHierarchy(type.Definition).ToList();
                var boundHierarchy = 
                    hierarchy.Select(x => bindings.Types.FirstOrDefault(y => y.Name == x.Item2.FullName))
                    .Where(x => x != null)
                    .ToList();

                var baseType = boundHierarchy.Count > 1 ? boundHierarchy[1] : null;

                // All properties and events apart from the attached ones
                var allBaseMembers = (from x in boundHierarchy.Skip(1) from y in x.Members select y).ToList();
                var allImmediateMembers = type.Members.ToList();
                var allMembers = allImmediateMembers.Concat(allBaseMembers);

                // Emit the constructor
                w.WriteLine();
                w.WriteLine($"    /// Describes a {nameOfCreator} in the view");
                w.Write($"    static member {nameOfCreator}(");
                head = "";
                foreach (var m in allMembers)
                {
                    var inputType = m.GetInputType(bindings, null);

                    w.Write($"{head}?{m.LowerBoundShortName}: {inputType}");
                    head = ", ";
                }
                w.WriteLine($") = ");
                w.WriteLine($"        let attribs = [| ");
                foreach (var m in allMembers)
                {
                    var conv = string.IsNullOrWhiteSpace(m.Conv) ? "" : m.Conv;
                    w.WriteLine("            match " + m.LowerBoundShortName + " with None -> () | Some v -> yield (\"" + m.BoundUniqueName + "\"" + $", box (" + conv + "(v))) ");
                }
                w.WriteLine($"          |]");

                var ctor = tdef.Methods
                    .Where(x => x.IsConstructor && x.IsPublic)
                    .OrderBy(x => x.Parameters.Count)
                    .FirstOrDefault();

                w.WriteLine();
                w.WriteLine($"        let create () =");
                if (!tdef.IsAbstract && ctor != null && ctor.Parameters.Count == 0)
                {
                    w.WriteLine($"            box (new {customTypeToCreate}())");
                }
                else
                {
                    w.WriteLine($"            failwith \"can'tdef create {tdef.FullName}\"");
                }
                w.WriteLine();
                w.WriteLine($"        let apply (prevOpt: XamlElement option) (source: XamlElement) (target:obj) = ");

                if (baseType == null && type.Members.Count() == 0)
                {
                    w.WriteLine($"            ()");
                }
                else
                {
                    w.WriteLine($"            let target = (target :?> {tdef.FullName})");
                    foreach (var m in allMembers)
                    {
                        var apply = 
                            !string.IsNullOrWhiteSpace(m.Apply)
                               ? $"target.{m.Name} <- " + m.Apply
                               : !string.IsNullOrWhiteSpace(m.ApplyCode)
                                  ? m.ApplyCode
                                  : "";
                        var bt = ResolveGenericParameter(m.BoundType, hierarchy);
                        string elementType = m.GetElementType(hierarchy);
                        if (elementType != null && elementType != "obj" && string.IsNullOrWhiteSpace(apply))
                        {
                            w.WriteLine($"            let prevCollOpt = match prevOpt with None -> None | Some prev -> prev.Try{m.BoundUniqueName}");
                            w.WriteLine($"            match prevCollOpt, source.Try{m.BoundUniqueName} with");
                            w.WriteLine($"            // For structured objects, amortize on reference equality");
                            w.WriteLine($"            | Some prevColl, Some newColl when System.Object.ReferenceEquals(prevColl, newColl) -> ()");
                            w.WriteLine($"            | _, Some coll when coll <> null && coll.Length > 0 ->");
                            w.WriteLine($"                applyToIList");
                            w.WriteLine($"                    prevCollOpt");
                            w.WriteLine($"                    coll");
                            w.WriteLine($"                    target.{m.Name}");
                            w.WriteLine($"                    (fun (x:XamlElement) -> x.CreateAs{elementType}())");
                            if (m.Attached != null)
                            {
                                w.WriteLine($"                    (fun prevChildOpt newChild targetChild -> ");
                                foreach (var ap in m.Attached)
                                {
                                    w.WriteLine($"                        // Adjust the attached properties");
                                    w.WriteLine($"                        match (match prevChildOpt with None -> None | Some prevChild -> prevChild.Try{ap.BoundUniqueName}), newChild.Try{ap.BoundUniqueName} with");
                                    w.WriteLine($"                        | Some prev, Some v when prev = v -> ()");
                                    var apApply = string.IsNullOrWhiteSpace(ap.Apply) ? "" : ap.Apply + " ";
                                    w.WriteLine($"                        | prevOpt, Some value -> {tdef.FullName}.Set{ap.Name}(targetChild, {apApply}value)");
                                    w.WriteLine($"                        | Some _, None -> {tdef.FullName}.Set{ap.Name}(targetChild, {ap.Default}) // TODO: not always perfect, should set back to original default?");
                                    w.WriteLine($"                        | _ -> ()");
                                }
                                w.WriteLine($"                        ())");
                            }
                            else
                            {
                                w.WriteLine($"                    (fun _ _ _ -> ())");
                            }
                            w.WriteLine($"                    (fun (prevChild:XamlElement) (newChild:XamlElement) -> prevChild.TargetType = newChild.TargetType)");
                            w.WriteLine($"                    (fun prevChild newChild targetChild -> newChild.ApplyIncremental(prevChild, targetChild))");
                            w.WriteLine($"            | _ -> target.{m.Name}.Clear()");
                        }
                        else
                        {
                            if (bindings.FindType(bt.FullName) is TypeBinding b && string.IsNullOrWhiteSpace(apply))
                            {
                                if (bt.IsValueType)
                                {
                                    w.WriteLine($"            let prevChildOpt = match prevOpt with None -> None | Some prev -> prev.Try{m.BoundUniqueName}");
                                    w.WriteLine($"            match prevChildOpt, source.Try{m.BoundUniqueName} with");
                                    w.WriteLine($"            // For structured objects, amortize on reference equality");
                                    w.WriteLine($"            | Some prevChild, Some newChild when System.Object.ReferenceEquals(prevChild, newChild) -> ()");
                                    w.WriteLine($"            | _, Some newChild ->");
                                    w.WriteLine($"                target.{m.Name} <- newChild.CreateAs{bt.Name}()");
                                    w.WriteLine($"            | Some _, None ->");
                                    w.WriteLine($"                target.{m.Name} <- Unchecked.defaultof<_>");
                                    w.WriteLine($"            | None, None -> ()");
                                }
                                else
                                {
                                    w.WriteLine($"            let prevChildOpt = match prevOpt with None -> None | Some prev -> prev.Try{m.BoundUniqueName}");
                                    w.WriteLine($"            match prevChildOpt, source.Try{m.BoundUniqueName} with");
                                    w.WriteLine($"            // For structured objects, amortize on reference equality");
                                    w.WriteLine($"            | Some prevChild, Some newChild when System.Object.ReferenceEquals(prevChild, newChild) -> ()");
                                    w.WriteLine($"            | Some prevChild, Some newChild ->");
                                    w.WriteLine($"                newChild.ApplyIncremental(prevChild, target.{m.Name})");
                                    w.WriteLine($"            | None, Some newChild ->");
                                    w.WriteLine($"                target.{m.Name} <- newChild.CreateAs{bt.Name}()");
                                    w.WriteLine($"            | Some _, None ->");
                                    w.WriteLine($"                target.{m.Name} <- null;");
                                    w.WriteLine($"            | None, None -> ()");
                                }
                            }
                            else if ((bt.Name.EndsWith("Handler") || bt.Name.EndsWith("Handler`1") || bt.Name.EndsWith("Handler`2")) &&  string.IsNullOrWhiteSpace(apply))
                            {
                                w.WriteLine($"            let prevValueOpt = match prevOpt with None -> None | Some prev -> prev.Try{m.BoundUniqueName}");
                                w.WriteLine($"            match prevValueOpt, source.Try{m.BoundUniqueName} with");
                                w.WriteLine($"            | Some prevValue, Some value when System.Object.ReferenceEquals(prevValue, value) -> ()");
                                w.WriteLine($"            | Some prevValue, Some value -> target.{m.Name}.RemoveHandler(prevValue); target.{m.Name}.AddHandler(value)");
                                w.WriteLine($"            | None, Some value -> target.{m.Name}.AddHandler(value)");
                                w.WriteLine($"            | Some prevValue, None -> target.{m.Name}.RemoveHandler(prevValue)");
                                w.WriteLine($"            | None, None -> ()");
                            }
                            else
                            {
                                w.WriteLine($"            let prevValueOpt = match prevOpt with None -> None | Some prev -> prev.Try{m.BoundUniqueName}");
                                w.WriteLine($"            match prevValueOpt, source.Try{m.BoundUniqueName} with");
                                w.WriteLine($"            | Some prevValue, Some value when prevValue = value -> ()");
                                if (!string.IsNullOrWhiteSpace(apply))
                                {
                                    w.WriteLine($"            | prevOpt, Some value -> {apply}");
                                }
                                else
                                {
                                    w.WriteLine($"            | prevOpt, Some value -> target.{m.Name} <- value");
                                }
                                w.WriteLine($"            | Some _, None -> target.{m.Name} <- {m.Default} // TODO: not always perfect, should set back to original default?");
                                w.WriteLine($"            | None, None -> ()");
                            }
                        }
                    }
                }
                                
                w.WriteLine($"        new XamlElement(typeof<{tdef.FullName}>, create, apply, Map.ofArray attribs)");

            }
            w.WriteLine($"[<AutoOpen>]");
            w.WriteLine($"module XamlCreateExtensions = ");
            foreach (var type in bindings.Types)
            {
                var tdef = type.Definition;
                var tname = string.IsNullOrWhiteSpace(type.ModelName) ? tdef.Name : type.ModelName;
                var hierarchy = GetHierarchy(type.Definition).ToList();
                var boundHierarchy = hierarchy.Select(x => bindings.Types.FirstOrDefault(y => y.Name == x.Item2.FullName))
                            .Where(x => x != null)
                            .ToList();

                var ctor = tdef.Methods
                    .Where(x => x.IsConstructor && x.IsPublic)
                    .OrderBy(x => x.Parameters.Count)
                    .FirstOrDefault();

                if (!tdef.IsAbstract && ctor != null && ctor.Parameters.Count == 0)
                {
                    w.WriteLine();
                    w.WriteLine($"    /// Specifies a {tname} in the view description, initially with default attributes");
                    w.WriteLine($"    let {Char.ToLower(tname[0])}{tname.Substring(1)} = Xaml.{tname}()");
                }
            }
            return w.ToString ();
        }

        static public TypeReference ResolveGenericParameter (TypeReference tref, IEnumerable<Tuple<TypeReference, TypeDefinition>> hierarchy)
        {
            if (tref == null)
                return null;
            if (!tref.IsGenericParameter)
                return tref;
            var q =
                from b in hierarchy where b.Item1.IsGenericInstance
                let ps = b.Item2.GenericParameters
                let p = ps.FirstOrDefault(x => x.Name == tref.Name)
                where p != null
                let pi = ps.IndexOf(p)
                let args = ((GenericInstanceType)b.Item1).GenericArguments
                select ResolveGenericParameter (args[pi], hierarchy);
            return q.First ();
        }


        static PropertyDefinition FindProperty(string name, TypeDefinition type)
        {
            var q =
                from tdef in GetHierarchy(type)
                from p in tdef.Item2.Properties
                where p.Name == name
                select p;
            return q.FirstOrDefault ();
        }

        static EventDefinition FindEvent(string name, TypeDefinition type)
        {
            var q =
                from tdef in GetHierarchy(type)
                from p in tdef.Item2.Events
                where p.Name == name
                select p;
            return q.FirstOrDefault ();
        }

        static IEnumerable<Tuple<TypeReference, TypeDefinition>> GetHierarchy (TypeDefinition type)
        {
            var d = type;
            yield return Tuple.Create ((TypeReference)d, d);
            while (d.BaseType != null) {
                var r = d.BaseType;
                d = r.Resolve();
                yield return Tuple.Create (r, d);
            }
        }

        static AssemblyDefinition LoadAssembly (string path)
        {
            if (path.StartsWith("packages")) {
                var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = Path.Combine (user, ".nuget", path);
            }
            return AssemblyDefinition.ReadAssembly(path);
        }
    }
}
