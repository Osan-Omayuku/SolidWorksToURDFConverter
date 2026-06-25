using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace RobotURDFExporter
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "SOLIDWORKS to URDF - Complete Physics & Mesh Exporter";
            Console.WriteLine("Connecting to active SOLIDWORKS instance...");

            try
            {
                SldWorks swApp = null;
                try
                {
                    swApp = (Marshal.GetActiveObject("SldWorks.Application") as SldWorks);
                }
                catch (Exception)
                {
                    swApp = (Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application")) as SldWorks);
                }

                if (swApp == null)
                {
                    Console.WriteLine("Error: Could not connect to SOLIDWORKS.");
                    return;
                }

                ModelDoc2 swModel = (ModelDoc2)swApp.IActiveDoc2;
                if (swModel == null)
                {
                    Console.WriteLine("Error: Document window returned null. Check your Windows UAC privilege levels.");
                    return;
                }

                string documentTitle = swModel.GetTitle();
                string robotName = CleanName(documentTitle) + "_Robot";

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n[CONNECTED] Target Document: {documentTitle}");
                Console.WriteLine($"[INITIALIZED] Exporting Complete Physics & Mesh Workspace for: '{robotName}'");
                Console.ResetColor();

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
                FlattenAndFilterTree(rootComponent, structuralLinks);

                if (structuralLinks.Count == 0)
                {
                    Console.WriteLine("Warning: No components passed structural filters.");
                    return;
                }

                // Set up local directories for mesh deployment
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string meshDirectory = Path.Combine(baseDirectory, "meshes");
                if (!Directory.Exists(meshDirectory))
                {
                    Directory.CreateDirectory(meshDirectory);
                }

                Console.WriteLine($"\nProcessing {structuralLinks.Count} structural nodes. Extracting matrices and exporting STL meshes...");

                for (int i = 0; i < structuralLinks.Count; i++)
                {
                    Component2 currentComp = structuralLinks[i];
                    string currentLinkName = CleanName(currentComp.Name2);

                    // 1. GET THE UNDERLYING MODEL DOCUMENT
                    ModelDoc2 compModel = (ModelDoc2)currentComp.GetModelDoc2();

                    int status = -1;
                    double[] massProps = null;

                    double mass = 0.15;
                    double cx = 0, cy = 0, cz = 0;
                    double ixx = 0.01, iyy = 0.01, izz = 0.01;
                    double ixy = 0, ixz = 0, iyz = 0;

                    // 2. EXTRACT INERTIAL PROPERTIES VIA THE EXTENSION OBJECT
                    if (compModel != null)
                    {
                        massProps = (double[])compModel.Extension.GetMassProperties2(1, out status, false);
                    }

                    if (massProps != null && massProps.Length >= 12)
                    {
                        cx = massProps[0]; cy = massProps[1]; cz = massProps[2];
                        // Safeguard against empty or unassigned materials
                        mass = massProps[5] > 0.0001 ? massProps[5] : 0.15;
                        ixx = massProps[6]; iyy = massProps[7]; izz = massProps[8];
                        ixy = massProps[9]; ixz = massProps[10]; iyz = massProps[11];
                    }

                    // 3. AUTO-EXPORT STL GEOMETRY (Corrected ref modifiers)
                    if (compModel != null)
                    {
                        string stlPath = Path.Combine(meshDirectory, $"{currentLinkName}.stl");
                        int errors = 0;
                        int warnings = 0;

                        // Save out cleanly as an STL mesh representation using ModelDocExtension.SaveAs3
                        compModel.Extension.SaveAs3(
                            stlPath,
                            (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                            (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                            null,
                            null,
                            ref errors,
                            ref warnings
                        );
                        Console.WriteLine($" -> Exported Mesh: meshes/{currentLinkName}.stl");
                    }

                    // Add structural link with full physical geometry configurations
                    urdfBuilder.AddLink(currentLinkName, mass, cx, cy, cz, ixx, ixy, ixz, iyy, iyz, izz);

                    // 4. GENERATE KINEMATIC KNOT JOINTS
                    if (i > 0)
                    {
                        Component2 parentComp = structuralLinks[i - 1];
                        string parentLinkName = CleanName(parentComp.Name2);

                        MathTransform rootTransform = parentComp.Transform2;
                        MathTransform childTransform = currentComp.Transform2;

                        if (rootTransform != null && childTransform != null)
                        {
                            MathTransform relativeTransform = childTransform.IMultiply(rootTransform.IInverse());
                            double[] matrixData = (double[])relativeTransform.ArrayData;

                            double x = matrixData[9]; double y = matrixData[10]; double z = matrixData[11];
                            double r = Math.Atan2(matrixData[7], matrixData[8]);
                            double p = Math.Asin(-matrixData[6]);
                            double yaw = Math.Atan2(matrixData[3], matrixData[0]);

                            string jointName = $"{parentLinkName}_to_{currentLinkName}";
                            string jointType = (currentLinkName.Contains("ConnectBot") || currentLinkName.Contains("Foot")) ? "fixed" : "revolute";

                            urdfBuilder.AddJoint(jointName, parentLinkName, currentLinkName, x, y, z, r, p, yaw, jointType);
                        }
                    }
                }

                // 5. APPEND EMULATION AND TRANSMISSION EXTENSIONS
                urdfBuilder.AppendGazeboExtensions(structuralLinks);

                string exportPath = Path.Combine(baseDirectory, "robot_description_automated.urdf");
                urdfBuilder.SaveToFile(exportPath);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n==================================================");
                Console.WriteLine("   COMPLETE PHYSICAL & MESH GENERATION SUCCESS    ");
                Console.WriteLine("==================================================");
                Console.ResetColor();
                Console.WriteLine($"Saved Description: {exportPath}");
                Console.WriteLine($"Saved Mesh Folder: {meshDirectory}\n");
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
                if (string.IsNullOrEmpty(rawName) ||
                    rawName.IndexOf("bearing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    rawName.IndexOf("screw", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    rawName.IndexOf("bolt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    rawName.IndexOf("washer", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                list.Add(child);
                FlattenAndFilterTree(child, list);
            }
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
        private List<string> _extensions = new List<string>();

        public UrdfXmlBuilder(string robotName)
        {
            _robotName = robotName;
        }

        public void AddLink(string name, double mass, double cx, double cy, double cz, double ixx, double ixy, double ixz, double iyy, double iyz, double izz)
        {
            if (_links.Any(l => l.Contains($"name=\"{name}\""))) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"  <link name=\"{name}\">");

            // Structural Inertial Node Integration
            sb.AppendLine("    <inertial>");
            sb.AppendLine($"      <mass value=\"{mass:F6}\"/>");
            sb.AppendLine($"      <origin xyz=\"{cx:F6} {cy:F6} {cz:F6}\" rpy=\"0 0 0\"/>");
            sb.AppendLine($"      <inertia ixx=\"{ixx:F6}\" ixy=\"{ixy:F6}\" ixz=\"{ixz:F6}\" iyy=\"{iyy:F6}\" iyz=\"{iyz:F6}\" izz=\"{izz:F6}\"/>");
            sb.AppendLine("    </inertial>");

            // Visual Node pointing to exported STL configuration
            sb.AppendLine("    <visual>");
            sb.AppendLine("      <origin xyz=\"0 0 0\" rpy=\"0 0 0\"/>");
            sb.AppendLine("      <geometry>");
            sb.AppendLine($"        <mesh filename=\"package://robot_description/meshes/{name}.stl\"/>");
            sb.AppendLine("      </geometry>");
            sb.AppendLine("    </visual>");

            // Physical Collision bounding node pointing to exported STL configuration
            sb.AppendLine("    <collision>");
            sb.AppendLine("      <origin xyz=\"0 0 0\" rpy=\"0 0 0\"/>");
            sb.AppendLine("      <geometry>");
            sb.AppendLine($"        <mesh filename=\"package://robot_description/meshes/{name}.stl\"/>");
            sb.AppendLine("      </geometry>");
            sb.AppendLine("    </collision>");

            sb.AppendLine("  </link>");
            _links.Add(sb.ToString());
        }

        public void AddJoint(string jointName, string parent, string child, double x, double y, double z, double r, double p, double yaw, string type)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"  <joint name=\"{jointName}\" type=\"{type}\">");
            sb.AppendLine($"    <parent link=\"{parent}\"/>");
            sb.AppendLine($"    <child link=\"{child}\"/>");
            sb.AppendLine($"    <origin xyz=\"{x:F6} {y:F6} {z:F6}\" rpy=\"{r:F6} {p:F6} {yaw:F6}\"/>");
            if (type == "revolute")
            {
                sb.AppendLine("    <axis xyz=\"0 0 1\"/>");
                sb.AppendLine("    <limit effort=\"30.0\" lower=\"-1.5708\" upper=\"1.5708\" velocity=\"1.0\"/>");
            }
            sb.AppendLine("  </joint>");
            _joints.Add(sb.ToString());

            // Build hardware transmission extension for actuators if joint is active/revolute
            if (type == "revolute")
            {
                StringBuilder trans = new StringBuilder();
                trans.AppendLine($"  <transmission name=\"trans_{jointName}\">");
                trans.AppendLine("    <type>transmission_interface/SimpleTransmission</type>");
                trans.AppendLine($"    <joint name=\"{jointName}\">");
                trans.AppendLine("      <hardwareInterface>hardware_interface/EffortJointInterface</hardwareInterface>");
                trans.AppendLine("    </joint>");
                trans.AppendLine($"    <actuator name=\"motor_{jointName}\">");
                trans.AppendLine("      <mechanicalReduction>1</mechanicalReduction>");
                trans.AppendLine("    </actuator>");
                trans.AppendLine("  </transmission>");
                _extensions.Add(trans.ToString());
            }
        }

        public void AppendGazeboExtensions(List<Component2> links)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("  ");
            sb.AppendLine("  <gazebo>");
            sb.AppendLine("    <plugin name=\"gazebo_ros_control\" filename=\"libgazebo_ros_control.so\">");
            sb.AppendLine("      <robotNamespace>/</robotNamespace>");
            sb.AppendLine("    </plugin>");
            sb.AppendLine("  </gazebo>");

            foreach (var link in links)
            {
                string name = CleanName(link.Name2);
                sb.AppendLine($"  <gazebo reference=\"{name}\">");
                sb.AppendLine("    <mu1>0.2</mu1>");
                sb.AppendLine("    <mu2>0.2</mu2>");
                sb.AppendLine("    <selfCollide>true</selfCollide>");
                sb.AppendLine("  </gazebo>");
            }
            _extensions.Add(sb.ToString());
        }

        private string CleanName(string rawName)
        {
            string clean = rawName;
            int caretIdx = clean.IndexOf('^');
            if (caretIdx >= 0) clean = clean.Substring(0, caretIdx);
            int dashIdx = clean.LastIndexOf('-');
            if (dashIdx >= 0) clean = clean.Substring(0, dashIdx);
            return clean.Trim().Replace(" ", "_");
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
            xml.AppendLine();
            xml.AppendLine("  ");
            foreach (var ext in _extensions) xml.AppendLine(ext);
            xml.AppendLine("</robot>");
            File.WriteAllText(filePath, xml.ToString());
        }
    }
}