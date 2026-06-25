# SolidWorksToURDFConverter

A C# console application that interfaces directly with an active SOLIDWORKS instance to automatically crawl assembly structures, calculate relative spatial transforms, and export clean, Gazebo-compatible URDF (Unified Robot Description Format) files.

Instead of requiring complex sub-assembly nesting or manual coordinate mapping, this tool automatically flattens your design tree and chains sequential links together dynamically.

## Features
* **Live COM Connection:** Connects instantly to your active desktop SOLIDWORKS session via the Windows Running Object Table (ROT).
* **Automatic Kinematic Chaining:** Sequentially links flat component lists from parent to child, automatically calculating relative positioning and Euler angle orientations ($xyz$ / $rpy$).
* **Smart Component Filtering:** Automatically strips out heavy hardware noise (screws, bolts, washers, bearings) using string matching and an arbitrary mass-cutoff threshold (< 10 grams).

## Requirements & Setup

To allow the application to hook into the SOLIDWORKS API without throwing runtime errors or permissions exceptions, ensure the following configurations are set:

### 1. Visual Studio Reference Configuration
* Open the solution in Visual Studio.
* Expand **Dependencies** > **COM** (or **References**) in the Solution Explorer.
* Right-click both **`SolidWorks.Interop.sldworks`** and **`SolidWorks.Interop.swconst`** and select **Properties**.
* Change **Embed Interop Types** to **`False`**.

### 2. Match User Privilege Tiers (UAC)
Windows isolates COM processes running under different administrative privileges. 
* **Both programs must run at the exact same security level.**
* If SOLIDWORKS is running as a standard user, **do not** run Visual Studio as an Administrator. Mismatched permissions will cause the active document workspace to return a `null` connection error.

### 3. Workspace Readiness
* Ensure your target robot assembly file is open, fully visible, and active on your desktop before launching the converter.
* Ensure all structural links are **Fully Resolved** (not suppressed or frozen in lightweight mode) so the API can read geometric transformations and physical mass properties accurately.

## How To Run
1. Open your robot model in SOLIDWORKS.
2. Build and run the `SolidWorksToURDFConverter` console application from Visual Studio.
3. Find your generated `robot_description_automated.urdf` file output directly inside the application's `/bin/Debug/` execution folder.
