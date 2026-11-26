// Set 0 ABI 1. A slot is four 32-bit words in shader order. Adjacent
// scalar fields share a slot; byte offsets are derived from their widths.
export interface Field { glsl: string; host: string; type: string; words: number }
type Slot = Field[];
interface Entry {
  binding: string; name: string; host: string; size?: string; slots?: Slot[];
  details: [string, string]; resources?: [number, string, string][];
  flags?: { title: string; values: [string, string][] };
}

const vector = (glsl: string, host: string, type = 'vec4'): Slot => [{ glsl, host, type, words: 4 }];
const scalar = (glsl: string, host: string, type = 'float'): Field => ({ glsl, host, type, words: 1 });

export const abi: Entry[] = [
  {
    binding: 'Push', name: 'Camera', host: 'CameraPush', size: '96 B',
    slots: [
      vector('right', 'Right'), vector('up', 'Up'), vector('back', 'Back'), vector('pos', 'Pos'),
      vector('lens', 'Lens'), vector('dims', 'Dims', 'ivec4'),
    ],
    details: [
      'lens packs R, tan(vertical FOV/2), seconds, and a reserved word. dims holds width, height, node count, and probe count.',
      'lens 依次存放曲率半径、tan(垂直视场角/2)、秒数与保留字。dims 存放宽、高、节点数与探针数。',
    ],
  }, {
    binding: 'b0', name: 'output_image', host: 'image2D',
    resources: [[0, 'output_image', 'writeonly image2D (rgba8)']],
    details: [
      'Each compute invocation writes one output pixel. The renderer exposes the image as a Texture2DRD.',
      '每个计算调用写入一个输出像素，渲染器将其暴露为 Texture2DRD。',
    ],
  }, {
    binding: 'b1', name: 'Triangle', host: 'Faces (tris[]) -> TriangleRecord', size: '160 B',
    slots: [
      vector('plane', 'Plane'), vector('dual0', 'Dual0'), vector('dual1', 'Dual1'), vector('dual2', 'Dual2'),
      vector('inv_k_material', 'InvKMaterial'), vector('uv01', 'Uv01'), vector('uv2', 'Uv2'),
      vector('n0', 'N0'), vector('n1', 'N1'), vector('n2', 'N2'),
    ],
    details: [
      'inv_k_material.w is the material index. uv2.zw encodes surface index and traversal flags as numeric floats.',
      'inv_k_material.w 是材质索引。uv2.zw 用浮点数存放表面索引和遍历标志。',
    ],
    flags: { title: 'TraversalFlags (uv2.w)', values: [['0', 'CullBack'], ['1', 'CullFront'], ['2', 'TwoSided'], ['3', 'CullMask'], ['4', 'Cutout']] },
  }, {
    binding: 'b2', name: 'CapNode', host: 'Tree (nodes[]) -> CapRecord', size: '32 B',
    slots: [vector('center', 'Center'), vector('bounds', 'Bounds')],
    details: [
      'Unit cap center in ℝ⁴. bounds packs cos(α), first index, triangle count, and escape index.',
      '单位球冠中心位于 ℝ⁴。bounds 依次存放 cos(α)、起始索引、数量与跳出索引。',
    ],
  }, {
    binding: 'b3', name: 'MaterialRecord', host: 'Materials (material_records[]) -> MaterialRecord', size: '48 B',
    slots: [
      vector('base_color', 'BaseColor'),
      vector('emission_roughness', 'EmissionRoughness'),
      [scalar('metallic', 'Metallic'), scalar('alpha_cutoff', 'AlphaCutoff'), scalar('albedo_ref', 'AlbedoRef', 'uint'), scalar('flags', 'Flags', 'uint')],
    ],
    details: [
      'albedo_ref stores bank in high 8 bits and layer in low 24 bits. NoTexture is uint.MaxValue.',
      'albedo_ref 的高 8 位编码纹理库，低 24 位编码图层。NoTexture 等于 uint.MaxValue。',
    ],
    flags: { title: 'MaterialRecord.Flags', values: [['1', 'Unshaded'], ['0 / 2 / 4', 'CullMode << 1'], ['8', 'Nearest'], ['16', 'Clamp'], ['32', 'DisableMips']] },
  }, {
    binding: 'b4', name: 'Diagnostic', host: 'Diagnostics (probes[]) -> ProbeRecord', size: '64 B',
    slots: [
      vector('p', 'Pos'), vector('d', 'Direction'),
      [scalar('ids.x', 'Kind', 'int'), scalar('ids.y', 'Id', 'int'), scalar('angle', 'Angle'), scalar('u', 'U')],
      [scalar('v', 'V'), scalar('pad0', 'Pad0'), scalar('pad1', 'Pad1'), scalar('pad2', 'Pad2')],
    ],
    details: [
      'Writable diagnostic buffer pairing an input ray with its hit. GLSL ivec2 ids maps to two C# int fields.',
      '可写诊断缓冲区保存输入射线与命中结果，GLSL 的 ivec2 ids 对应两个 C# int 字段。',
    ],
  }, {
    binding: 'b5', name: 'LightRecord', host: 'Lights (lights[]) -> LightRecord', size: '32 B',
    slots: [vector('source', 'Source'), vector('radiance_cutoff', 'RadianceCutoff')],
    details: [
      'Follows a 16-byte uvec4 light_counts header. Record i starts at byte 16 + 32i.',
      '紧跟在 16 字节 uvec4 light_counts 头部之后，第 i 条记录起于字节 16 + 32i。',
    ],
  }, {
    binding: 'b6', name: 'Environment', host: 'env -> EnvironmentBlock', size: '80 B',
    slots: [
      vector('background', 'Background'),
      vector('ambient_energy', 'AmbientEnergy'),
      vector('fog_density', 'FogDensity'),
      vector('mode_ground_mat', 'ModeGroundMaterial', 'ivec4'),
      vector('ground_uv_xform', 'GroundUvTransform'),
    ],
    details: [
      'std140 uniform block holding shading mode and ground material in mode_ground_mat.xy. Negative ground index disables floor.',
      'std140 一致缓冲区在 mode_ground_mat.xy 中存放着色模式与地面材质索引，负值表示关闭地面。',
    ],
  }, {
    binding: 'b7–10', name: 'Texture banks', host: 'texture2DArray',
    resources: Array.from({ length: 4 }, (_, i) => [7 + i, 'bank' + i, 'texture2DArray']),
    details: [
      'Four independent array textures selected by albedo_ref.',
      '四张独立数组纹理，由 albedo_ref 指定库与图层。',
    ],
  }, {
    binding: 'b11–14', name: 'Samplers', host: 'sampler',
    resources: ['nearest_repeat', 'linear_repeat', 'nearest_clamp', 'linear_clamp'].map((name, i) => [11 + i, name, 'sampler']),
    details: [
      'Material flags choose filtering and UV addressing independently of the texture bank.',
      '材质标志分别选择过滤与寻址模式，与纹理库解耦。',
    ],
  },
];
