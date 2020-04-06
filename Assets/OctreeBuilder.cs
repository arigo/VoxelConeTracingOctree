using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;


//[ExecuteInEditMode]
public class OctreeBuilder : MonoBehaviour
{
    public Light directionalLight;
    public LayerMask cullingMask = -1;
    public int gridResolution;
    public float gridPixelSizeLevel0;
    public int gridLevels;
    public int computeBufferSize;
    public int maxComputeSteps;
    public Shader octreeBuilderShader;
    public ComputeShader octreeFillShader;

    //public int[] _octree_dump;


    Camera _shadowCam;
    ComputeBuffer octree;


    void OnDestroy()
    {
        if (octree != null)
            octree.Release();
    }


    RenderTexture GetTemporaryTarget()
    {
        return RenderTexture.GetTemporary(gridResolution, gridResolution, 0,
                                          RenderTextureFormat.R8);
    }

    const int OCTREE_ROOT = 8;

    void MakeOctreeRecursively(List<int> nodes, int target, int levels)
    {
        for (int i = 0; i < 8; i++)
        {
            nodes[target + i] = nodes.Count;
            for (int j = 0; j < 8; j++)
                nodes.Add(0);
        }
        levels--;
        if (levels > 0)
            for (int i = 0; i < 8; i++)
                MakeOctreeRecursively(nodes, nodes[target + i], levels);
    }

    void BuildOctree()
    {
        if (octree != null)
        {
            octree.Release();
            octree = null;
        }
        var cam = FetchShadowCamera();
        var trackTransform = directionalLight.transform;
        cam.transform.SetPositionAndRotation(trackTransform.position, trackTransform.rotation);

        /* _VCT_WorldToOctreeMatrix: maps the cube in the directionalLight's orientation of size
         *
         *     2**gridLevels * gridPixelSizeLevel0 * gridResolution
         *
         * to a cube in [0,1]**3.  Note that the last rendered grid level is 'gridLevels - 1',
         * so only the inner [0.25,0.75]**3 will ever be occupied.  That's what we want, with
         * the outermost 0.25-sized cubes containing incoming radiance from the sky.
         */
        /*float octree_total_size = (1 << gridLevels) * gridResolution * gridPixelSizeLevel0;
        Matrix4x4 world_to_octree_matrix = Matrix4x4.Scale(Vector3.one / octree_total_size) *
                                           cam.transform.worldToLocalMatrix;
        Shader.SetGlobalMatrix("_VCT_WorldToOctreeMatrix", world_to_octree_matrix);
        Shader.SetGlobalInt("_VCT_GridResolution", gridResolution);*/


        octree = new ComputeBuffer(computeBufferSize, 4, ComputeBufferType.Counter);
        int fill_kernel = octreeFillShader.FindKernel("Fill");
        int thread_group = (computeBufferSize + 63) / 64;
        octreeFillShader.SetBuffer(fill_kernel, "Octree", octree);
        octreeFillShader.Dispatch(fill_kernel, thread_group, 1, 1);

        /*var initial_octree = new List<int>();
        for (int j = 0; j < OCTREE_ROOT + 8; j++)
            initial_octree.Add(0);
        MakeOctreeRecursively(initial_octree, OCTREE_ROOT, levels: 2);
        octree.SetData(initial_octree);*/

        var target = GetTemporaryTarget();
        cam.targetTexture = target;
        Graphics.SetRandomWriteTarget(1, octree);

        float global_scale = 0.5f / gridResolution;
        int tree_levels = (int)Mathf.Log(gridResolution, 2);
        int level = gridLevels - 1;
        int orientation_mask = 7;

        int max_steps = maxComputeSteps;

        while (level >= 0)
        {
            float half_size = (1 << level) * gridResolution * gridPixelSizeLevel0 * 0.5f;
            cam.orthographicSize = half_size;
            cam.nearClipPlane = -half_size;
            cam.farClipPlane = half_size;
            /* in the shader: xyz *= _VCT_Scale.xxy;
                              xyz += _VCT_Scale.zzw;
               should turn the value (gridResolution/2, gridResolution/2, 1/2)
               to (1/2, 1/2, 1/2)
             */
            Vector4 vct_scale = new Vector4(global_scale, global_scale * gridResolution,
                            0.5f - gridResolution * 0.5f * global_scale,
                            0.5f - gridResolution * 0.5f * global_scale);
            //Debug.Log("vct_scale: " + vct_scale.x + " " + vct_scale.y + " " + vct_scale.z + " " + vct_scale.w);
            Shader.SetGlobalVector("_VCT_Scale", vct_scale);
            Shader.SetGlobalInt("_VCT_TreeLevels", tree_levels);

            var orig_rotation = cam.transform.rotation;
            var axis_y = cam.transform.up;
            var axis_z = cam.transform.forward;

            if ((orientation_mask & 1) != 0)
            {
                cam.RenderWithShader(octreeBuilderShader, "RenderType");
            }
            if ((orientation_mask & 2) != 0)
            {
                cam.transform.Rotate(axis_y, -90, Space.World);
                cam.transform.Rotate(axis_z, -90, Space.World);
                Shader.EnableKeyword("ORIENTATION_2");
                cam.RenderWithShader(octreeBuilderShader, "RenderType");
                Shader.DisableKeyword("ORIENTATION_2");
                cam.transform.rotation = orig_rotation;
            }
            if ((orientation_mask & 4) != 0)
            {
                cam.transform.Rotate(axis_z, 90, Space.World);
                cam.transform.Rotate(axis_y, 90, Space.World);
                Shader.EnableKeyword("ORIENTATION_3");
                cam.RenderWithShader(octreeBuilderShader, "RenderType");
                Shader.DisableKeyword("ORIENTATION_3");
                cam.transform.rotation = orig_rotation;
            }

            var flag = new int[3];
            octree.GetData(flag, 0, 0, 3);
            orientation_mask = 0;
            orientation_mask |= flag[0] != 0 ? 1 : 0;
            orientation_mask |= flag[1] != 0 ? 2 : 0;
            orientation_mask |= flag[2] != 0 ? 4 : 0;
            if (orientation_mask == 0)
            {
                tree_levels += 1;
                level -= 1;
                global_scale *= 0.5f;
                orientation_mask = 7;
            }
            //_octree_dump = new int[592];
            //octree.GetData(_octree_dump);
            flag[0] = flag[1] = flag[2] = 0;
            octree.SetData(flag, 0, 0, 3);


            if (--max_steps <= 0)
                break;
        }

        cam.targetTexture = null;
        RenderTexture.ReleaseTemporary(target);
    }

    Camera FetchShadowCamera()
    {
        if (_shadowCam == null)
        {
            // Create the shadow rendering camera
            GameObject go = new GameObject("shadow cam (not saved)");
            //go.hideFlags = HideFlags.HideAndDontSave;
            go.hideFlags = HideFlags.DontSave;

            _shadowCam = go.AddComponent<Camera>();
            _shadowCam.orthographic = true;
            _shadowCam.enabled = false;
            _shadowCam.clearFlags = CameraClearFlags.Nothing;
            _shadowCam.aspect = 1;
            /* Obscure: if the main camera is stereo, then this one will be confused in
             * the SetTargetBuffers() mode unless we force it to not be stereo */
            _shadowCam.stereoTargetEye = StereoTargetEyeMask.None;
        }
        _shadowCam.cullingMask = cullingMask;
        return _shadowCam;
    }


    void Update()
    {
        BuildOctree();
    }

    void OnDrawGizmos()
    {
        if (octree == null)
            return;
        var _octree_dump = new int[computeBufferSize];
        octree.GetData(_octree_dump);

        Gizmos.matrix = directionalLight.transform.localToWorldMatrix;

        void DrawRec(int index, Vector3 center, float halfscale)
        {
            float quaterscale = halfscale * 0.5f;
            bool leaf = true;
            for (int i = 0; i < 8; i++)
                if (_octree_dump[index + i] >= index + 8)
                {
                    leaf = false;
                    Vector3 delta = quaterscale * Vector3.one;
                    if ((i & 1) == 0) delta.x = -delta.x;
                    if ((i & 2) == 0) delta.y = -delta.y;
                    if ((i & 4) == 0) delta.z = -delta.z;
                    DrawRec(_octree_dump[index + i], center + delta, quaterscale);
                }
                else
                    Debug.Assert(_octree_dump[index + i] == 0, "back-link at " + (index + i));
            if (leaf)
            {
                Gizmos.color = new Color(1, 0.7f, 0.7f);
                Gizmos.DrawWireCube(center, halfscale * 2 * Vector3.one);
            }
            else
            {
                /*Gizmos.color = Color.red;
                Gizmos.DrawWireCube(center, halfscale * 2 * Vector3.one);*/
            }
        }

        DrawRec(OCTREE_ROOT, Vector3.zero, 0.5f * (1 << gridLevels) * gridPixelSizeLevel0 * gridResolution);

        for (int i = 0; i < gridLevels; i++)
        {
            Gizmos.color = new Color(1f, 1f, 1f);
            Gizmos.DrawWireCube(Vector3.zero, (1 << i) * gridPixelSizeLevel0 * gridResolution * Vector3.one);
        }
    }
}
