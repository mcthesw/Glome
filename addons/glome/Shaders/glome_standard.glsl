#[compute]
#version 450
#include "res://addons/glome/Shaders/glome_abi.glslinc"
#include "res://addons/glome/Shaders/glome_camera.glslinc"
#include "res://addons/glome/Shaders/glome_intersections.glslinc"
#include "res://addons/glome/Shaders/glome_lighting.glslinc"

vec3 illuminate(MaterialRecord m, vec3 albedo, vec4 x, vec4 n, vec4 geometric, vec4 view, int ignore) {
    vec3 color = env.ambient_energy.rgb*env.ambient_energy.w*albedo*(1.0-m.metallic);
    uint directions = light_counts.x+light_counts.y, unshadowed_end = directions+light_counts.z;
    for (uint i = 0u; i < unshadowed_end+light_counts.w; ++i) {
        LightRecord light = lights[i];
        float angle;
        vec4 l = light_direction(x,light.source,angle);
        if (dot(l,n) <= 0.0 || dot(l,geometric) <= 0.0) continue;
        float attenuation = 1.0;
        if (i >= directions) {
            if (dot(x,light.source) <= light.radiance_cutoff.w) continue;
            float range = acos(clamp(light.radiance_cutoff.w,-1.0,1.0));
            float falloff = 1.0-angle/max(range,1e-6);
            attenuation = falloff*falloff;
        }
        bool shadowed = (i >= light_counts.x && i < directions) || i >= unshadowed_end;
        if (shadowed && !light_visible(x,geometric,light.source,ignore)) continue;
        color += direct_light(m,albedo,n,view,l)*light.radiance_cutoff.rgb*attenuation;
    }
    return color;
}

// The storage image is UNORM; convert linear scene radiance once at presentation.
vec3 to_display(vec3 rgb) {
    rgb = max(rgb,vec3(0));
    return mix(12.92*rgb,1.055*pow(rgb,vec3(1.0/2.4))-0.055,greaterThan(rgb,vec3(0.0031308)));
}

vec3 standard_view(vec4 d) {
    Hit h = trace_primary(camera.pos,d);
    if (h.kind == 0) return to_display(geodesic_fog(env.background.rgb,h.t));
    vec4 x = normalize(camera.pos*cos(h.t)+d*sin(h.t));
    vec4 incoming = d*cos(h.t)-camera.pos*sin(h.t);
    vec4 geometric = vec4(0,1,0,0), n = geometric;
    float pixel_arc = abs(sin(h.t))*2.0*camera.lens.y/float(camera.dims.y);
    int material = env.mode_ground_mat.y;
    float footprint;
    if (h.kind == 2) {
        Triangle tri = tris[h.id];
        material = int(tri.inv_k_material.w);
        geometric = tri.plane;
        n = smooth_normal(tri,h.weights,x);
        footprint = uv_footprint(tri,x,h.uv,pixel_arc/max(0.01,abs(dot(geometric,incoming))));
    } else {
        footprint = pixel_arc*camera.lens.x*max(abs(env.ground_uv_xform.x),abs(env.ground_uv_xform.y))/max(0.01,abs(incoming.y));
    }
    if (dot(geometric,incoming) > 0.0) geometric = -geometric;
    if (dot(n,geometric) < 0.0) n = -n;
    if (env.mode_ground_mat.x == 2) {
        if (h.kind == 1) {
            vec2 arcs = asin(clamp(x.xz,-1.0,1.0))*camera.lens.x;
            vec2 grid = abs(fract(arcs+0.5)-0.5);
            return vec3(0.57,0.59,0.59)*mix(0.84,1.0,smoothstep(0.015,0.06,min(grid.x,grid.y)));
        }
        return vec3(0.76,0.75,0.72)*(0.40+0.14*n.y+0.44*max(0.0,dot(n,vec4(0.48,0.8,0.36,0))));
    }
    MaterialRecord m = material_records[material];
    vec3 albedo = material_sample(m,h.uv,footprint).rgb;
    vec3 color = env.mode_ground_mat.x == 1 || (m.flags & 1u) != 0u ? albedo :
        illuminate(m,albedo,x,n,geometric,-incoming,h.id);
    return to_display(geodesic_fog(color+m.emission_roughness.rgb,h.t));
}

void main() {
    ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
    if (camera.dims.w != 0) {
        if (pixel.y == 0 && pixel.x < camera.dims.w)
            write_probe(pixel.x,trace_primary(probes[pixel.x].p,probes[pixel.x].d));
        return;
    }
    if (any(greaterThanEqual(pixel,camera.dims.xy))) return;
    imageStore(output_image,pixel,vec4(standard_view(pixel_direction(screen_uv(pixel))),1));
}
