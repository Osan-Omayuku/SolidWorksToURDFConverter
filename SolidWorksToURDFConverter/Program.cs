using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices; // Required for Marshal.GetActiveObject connection tracking
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace RobotURDFExporter
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "SOLIDWORKS - Fully Automated Sequential Tree-Walker";
            Console.WriteLine("Connecting to active SOLIDWORKS instance via ROT...");

            try
            {
                SldWorks swApp = null;

                try
                {
                    // Force connection to your currently running visible active window instance
                    swApp = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
                }
                catch (Exception)
                {
                    // Fallback to creation if no open instance is registered globally
                    swApp = (SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application"));
                }

                if (swApp == null)
                {
                    Console.WriteLine("Error: Could not connect to SOLIDWORKS. Ensure the software is running.");
                    return;
                }

                ModelDoc2 swModel = (ModelDoc2)swApp.IActiveDoc2;
                if (swModel == null)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("\n[PERMISSIONS CHECK] Document window returned null.");
                    Console.WriteLine("If SOLIDWORKS is open, close Visual Studio and reopen it WITHOUT 'Run as Administrator'.");
                    Console.WriteLine("Both programs must run at the same UAC privilege level to share data.");
                    Console.ResetColor();
                    return;
                }

                string documentTitle = swModel.GetTitle();
                string robotName = CleanName(documentTitle) + "_Robot";

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n[CONNECTED] Target Document: {documentTitle}");
                Console.WriteLine($"[INITIALIZED] Auto-Chaining Kinematic URDF for: '{robotName}'");
                Console.ResetColor();

                // Unsuppress components to read physical matrices accurately
                if (swModel.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    AssemblyDoc swAssy = (AssemblyDoc)swModel;
                    swAssy.ResolveAllLightweight();
                }

                Configuration swConfig = (Configuration)swModel.GetActiveConfiguration();
                Component2 rootComponent = (Component2)swConfig.GetRootComponent3(true);

                UrdfXmlBuilder urdfBuilder = new UrdfXmlBuilder(robotName);
                List<Component2> structuralLinks = new List<Component2>();

                Console.WriteLine("\nDiscovering and filtering active design tree components...");

                // Collect all valid parts from the assembly tree sequentially
                FlattenAndFilterTree(rootComponent, structuralLinks);

                if (structuralLinks.Count == 0)
                {
                    Console.WriteLine("Warning: No components passed the structural filters (Check mass/naming filters).");
                    return;
                }

                Console.WriteLine($"\nProcessing {structuralLinks.Count} structural elements into an ordered kinematic chain...");

                // Process the collected parts sequentially down the structural leg list
                for (int i = 0; i < structuralLinks.Count; i++)
                {
                    Component2 currentComp = structuralLinks[i];
                    string currentLinkName = CleanName(currentComp.Name2);
                    double currentMass = GetComponentMass(currentComp);

                    // Add structural link node to XML dictionary list
                    urdfBuilder.AddLink(currentLinkName, currentMass);

                    // Link current part back to the component immediately preceding it in the tree chain
                    if (i > 0)
                    {
                        Component2 parentComp = structuralLinks[i - 1];
                        string parentLinkName = CleanName(parentComp.Name2);

                        MathTransform rootTransform = parentComp.Transform2;
                        MathTransform childTransform = currentComp.Transform2;

                        if (rootTransform != null && childTransform != null)
                        {
                            // Transform math using your validated matrix offset formulas
                            MathTransform relativeTransform = childTransform.IMultiply(rootTransform.IInverse());
                            double[] matrixData = (double[])relativeTransform.ArrayData;

                            double x = matrixData[9]; double y = matrixData[10]; double z = matrixData[11];
                            double r = Math.Atan2(matrixData[7], matrixData[8]);
                            double p = Math.Asin(-matrixData[6]);
                            double yaw = Math.Atan2(matrixData[3], matrixData[0]);

                            string jointName = $"{parentLinkName}_to_{currentLinkName}";

                            // Dynamically flag base mounting brackets or feet as fixed, others as revolute joints
                            string jointType = (currentLinkName.Contains("ConnectBot") || currentLinkName.Contains("Foot")) ? "fixed" : "revolute";

                            urdfBuilder.AddJoint(jointName, parentLinkName, currentLinkName, x, y, z, r, p, yaw, jointType);
                            Console.WriteLine($" -> Auto-Chained: [{parentLinkName}] ===> [{currentLinkName}] ({jointType})");
                        }
                    }
                }

                string exportPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "robot_description_automated.urdf");
                urdfBuilder.SaveToFile(exportPath);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n==================================================");
                Console.WriteLine("     FULL AUTOMATED URDF GENERATION COMPLETE      ");
                Console.WriteLine("==================================================");
                Console.ResetColor();
                Console.WriteLine($"Saved file to: {exportPath}\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nRuntime Exception: {ex.Message}");
            }

            Console.WriteLine("\nExecution finished. Press [ENTER] to exit.");
            Console.ReadLine();
        }

        static void FlattenAndFilterTree(Component2 comp, List<Component2> list)
        {
            if (comp == null) return;

            object[] children = (object[])comp.GetChildren();
            if (children == null) return;

            foreach (object childObj in children)
            {
                Component2 child = (Component2)childObj;
                if (child == null) continue;

                string rawName = child.Name2;

                // Ignore hardware fasteners, bearings, or reference layouts
                if (string.IsNullOrEmpty(rawName) ||
                    rawName.IndexOf("bearing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    rawName.IndexOf("screw", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    rawName.IndexOf("bolt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    rawName.IndexOf("washer", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                // Ignore components beneath 10 grams to keep description clutter-free
                double mass = GetComponentMass(child);
                if (mass < 0.01)
                {
                    continue;
                }

                list.Add(child);

                // Recursively traverse downwards in case a subassembly folder is embedded
                FlattenAndFilterTree(child, list);
            }
        }

        static double GetComponentMass(Component2 comp)
        {
            ModelDoc2 compModel = (ModelDoc2)comp.GetModelDoc2();
            if (compModel == null) return 0.15;

            ModelDocExtension modelExt = compModel.Extension;
            if (modelExt == null) return 0.15;

            MassProperty massProps = modelExt.CreateMassProperty();
            if (massProps == null) return 0.15;

            return massProps.Mass > 0.001 ? massProps.Mass : 0.15;
        }

        static string CleanName(string rawName)
        {
            string clean = rawName;
            int caretIdx = clean.IndexOf('^');
            if (caretIdx >= 0) clean = clean.Substring(0, caretIdx);
            int dashIdx = clean.LastIndexOf('-');
            if (dashIdx >= 0) clean = clean.Substring(0, dashIdx);
            int arrowIdx = clean.IndexOf('<');
            if (arrowIdx >= 0) clean = clean.Substring(0, arrowIdx);
            return clean.Trim().Replace(" ", "_");
        }
    }

    public class UrdfXmlBuilder
    {
        private string _robotName;
        private List<string> _links = new List<string>();
        private List<string> _joints = new List<string>();

        public UrdfXmlBuilder(string robotName)
        {
            _robotName = robotName;
        }

        public void AddLink(string name, double mass)
        {
            if (_links.Any(l => l.Contains("name=\"" + name + "\""))) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("  <link name=\"" + name + "\">");
            sb.AppendLine("    <inertial>");
            sb.AppendLine("      <mass value=\"" + mass.ToString("F4") + "\"/>");
            sb.AppendLine("      <inertia ixx=\"0.01\" ixy=\"0.0\" ixz=\"0.0\" iyy=\"0.01\" iyz=\"0.0\" izz=\"0.01\"/>");
            sb.AppendLine("    </inertial>");
            sb.AppendLine("  </link>");
            _links.Add(sb.ToString());
        }

        public void AddJoint(string jointName, string parent, string child, double x, double y, double z, double r, double p, double yaw, string type)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"  <joint name=\"{jointName}\" type=\"{type}\">");
            sb.AppendLine($"    <parent link=\"{parent}\"/>");
            sb.AppendLine($"    <child link=\"{child}\"/>");
            sb.AppendLine($"    <origin xyz=\"{x:F4} {y:F4} {z:F4}\" rpy=\"{r:F4} {p:F4} {yaw:F4}\"/>");
            if (type == "revolute")
            {
                sb.AppendLine("    <axis xyz=\"0 0 1\"/>");
                sb.AppendLine("    <limit effort=\"30.0\" lower=\"-1.5708\" upper=\"1.5708\" velocity=\"1.0\"/>");
            }
            sb.AppendLine("  </joint>");
            _joints.Add(sb.ToString());
        }

        public void SaveToFile(string filePath)
        {
            StringBuilder xml = new StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" ?>");
            xml.AppendLine($"<robot name=\"{_robotName}\">");
            xml.AppendLine();
            xml.AppendLine("  ");
            foreach (var link in _links) xml.AppendLine(link);
            xml.AppendLine();
            xml.AppendLine("  ");
            foreach (var joint in _joints) xml.AppendLine(joint);
            xml.AppendLine("</robot>");
            File.WriteAllText(filePath, xml.ToString());
        }
    }
}