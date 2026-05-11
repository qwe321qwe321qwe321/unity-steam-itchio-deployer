using UnityEditor;
using UnityEngine;

namespace SteamItchIoDeployer
{
	[CustomEditor(typeof(BuildDeployConfig))]
	public class BuildDeployConfigEditor : Editor
	{
		public override void OnInspectorGUI()
		{
			DrawDefaultInspector();

			EditorGUILayout.Space(8);
			if (GUILayout.Button("Open Deploy Window", GUILayout.Height(28)))
				SteamItchIoDeployWindow.OpenWindowWithConfig((BuildDeployConfig)target);
		}
	}
}
