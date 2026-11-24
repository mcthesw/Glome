#[compute]
#version 450
#include "res://addons/glome/Shaders/glome_abi.glslinc"
#include "res://addons/glome/Shaders/glome_camera.glslinc"
#include "res://addons/glome/Shaders/glome_intersections.glslinc"
#include "res://addons/glome/Shaders/glome_lighting.glslinc"

const vec3 ink = vec3(0.12, 0.14, 0.25);
const vec3 paper = vec3(0.98, 0.91, 0.74);

vec3 illustration(vec4 direction) {
    Hit hit = trace_primary(camera.pos, direction);
    if (hit.kind == 0) return paper;
    vec4 point = normalize(camera.pos * cos(hit.t) + direction * sin(hit.t));
    vec4 incoming = direction * cos(hit.t) - camera.pos * sin(hit.t);
    vec4 normal = vec4(0, 1, 0, 0);
    vec4 geometric = normal;
    int id = env.mode_ground_mat.y;
    if (hit.kind == 2) {
        Triangle face = tris[hit.id];
        id = int(face.inv_k_material.w);
        normal = smooth_normal(face, hit.weights, point);
        geometric = face.plane;
    }
    if (dot(geometric, incoming) > 0.0) geometric = -geometric;
    if (dot(normal, geometric) < 0.0) normal = -normal;
    vec3 paint = sqrt(material_sample(material_records[id], hit.uv, 0.0).rgb);
    float light = 0.0;
    if (light_counts.x + light_counts.y > 0u) {
        float arc;
        vec4 sun = light_direction(point, lights[0].source, arc);
        light = max(dot(sun, normal), 0.0);
        if (light > 0.0 && !light_visible(point, geometric, lights[0].source, hit.id))
            light = 0.0;
    }
    vec3 shade = mix(ink, paint, 0.38);
    vec3 midtone = mix(paint, paper, 0.12);
    vec3 highlight = mix(paint, paper, 0.48);
    vec3 color = light < 0.18 ? shade : (light < 0.70 ? midtone : highlight);
    if (hit.kind == 2 && abs(dot(normal, incoming)) < 0.16) color = ink;
    float aerial = 1.0 - exp(-0.035 * camera.lens.x * hit.t);
    return mix(color, paper, aerial);
}

void main() {
    ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(pixel, camera.dims.xy))) return;
    vec4 direction = pixel_direction(screen_uv(pixel));
    imageStore(output_image, pixel, vec4(illustration(direction), 1.0));
}
