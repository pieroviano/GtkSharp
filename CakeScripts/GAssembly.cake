using System;
using System.Linq;
using P = System.IO.Path;
using F = System.IO.File;

public class GAssembly
{
    private ICakeContext Cake;

    public bool Init { get; private set; }
    public string Name { get; private set; }
    public string Dir { get; private set; }
    public string GDir { get; private set; }
    public string Csproj { get; private set; }
    public string RawApi { get; private set; }
    public string Metadata { get; private set; }

    public string[] Deps { get; set; }
    public string ExtraArgs { get; set; }

    // Vendored GObject-Introspection input, e.g. "Source/Gir/Gtk-4.0.gir".
    // Only consumed by the RegenerateApi target, never by a normal build:
    // the api.xml it produces is checked in, so building stays hermetic and
    // needs no Gtk installed.
    public string Gir { get; set; }

    // Extra .gir files passed to the converter for type resolution that are not
    // themselves wrapper assemblies -- GObject-2.0.gir is the standard case,
    // since GObject types are bound inside GLibSharp rather than separately.
    public string[] GirIncludes { get; set; }

    // Fail the build when a .metadata rule matches nothing. Turned on per
    // assembly in Phase 4, once that assembly's rules have been triaged --
    // before then every assembly has known-stale rules and this would just
    // block the build.
    public bool StrictMetadata { get; set; }

    public GAssembly(string name)
    {
        Cake = Settings.Cake;
        Deps = new string[0];
        GirIncludes = new string[0];

        Name = name;
        Dir = P.Combine("Source", "Libs", name);
        GDir = P.Combine(Dir, "Generated");

        var temppath = P.Combine(Dir, name);
        Csproj = temppath + ".csproj";
        RawApi = temppath + "-api.xml";
        Metadata = temppath + ".metadata";
    }

    // Rewrites the CHECKED-IN api.xml from the vendored .gir. Deliberately not
    // part of Prepare: regenerating an api.xml is an explicit, reviewable,
    // committed act, the same discipline the repository already had when the
    // files came from gapi2xml.pl by hand.
    public void RegenerateApi()
    {
        if (string.IsNullOrEmpty(Gir))
            return;

        if (!Cake.FileExists(Gir))
        {
            Cake.Error("Missing gir input for " + Name + ": " + Gir);
            throw new Exception("Missing gir input: " + Gir);
        }

        // Dependencies are passed for type resolution only; they are not emitted.
        var includes = string.Empty;
        foreach (var dep in Deps)
        {
            var depAssembly = Settings.AssemblyList.FirstOrDefault(a => a.Name == dep);

            if (depAssembly != null && !string.IsNullOrEmpty(depAssembly.Gir))
                includes += " --include=" + depAssembly.Gir;
        }

        foreach (var extra in GirIncludes)
            includes += " --include=" + extra;

        Cake.DotNetExecute("BuildOutput/Tools/GirToGapi.dll",
            "--gir=" + Gir + " " +
            "--out=" + RawApi + " " +
            "--assembly-name=" + Name +
            includes
        );
    }

    public void Prepare()
    {
        Cake.CreateDirectory(GDir);
        var tempapi = P.Combine(GDir, Name + "-api.xml");
        Cake.CopyFile(RawApi, tempapi);

        // Metadata file found, time to generate some stuff!!!
        if (Cake.FileExists(Metadata))
        {
            // Fixup API file
            var symfile = P.Combine(Dir, Name + "-symbols.xml");
            Cake.DotNetExecute("BuildOutput/Tools/GapiFixup.dll", 
                "--metadata=" + Metadata + " " + "--api=" + tempapi + 
                (Cake.FileExists(symfile) ? " --symbols=" + symfile : string.Empty) +
                (StrictMetadata ? " --strict" : string.Empty)
            );

            var extraargs = ExtraArgs + " ";

            // Locate APIs to include
            foreach(var dep in Deps)
            {
                var ipath = P.Combine("Source", "Libs", dep, "Generated", dep + "-api.xml");

                if (Cake.FileExists(ipath))
                    extraargs += " --include=" + ipath + " ";
            }

            // Generate code
            Cake.DotNetExecute("BuildOutput/Tools/GapiCodegen.dll", 
                "--outdir=" + GDir + " " +
                "--schema=Source/Libs/Shared/Gapi.xsd " +
                extraargs + " " +
                "--assembly-name=" + Name + " " +
                "--generate=" + tempapi
            );
        }

        Init = true;
    }

    public void Clean()
    {
        if (Cake.DirectoryExists(GDir))
            Cake.DeleteDirectory(GDir, new DeleteDirectorySettings { Recursive = true, Force = true });
    }
}
