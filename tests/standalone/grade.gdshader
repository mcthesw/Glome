shader_type canvas_item;
render_mode unshaded;

void fragment() {
    vec3 source = texture(TEXTURE, UV).rgb;
    float luma = dot(source, vec3(0.2126, 0.7152, 0.0722));
    vec3 shadows = vec3(0.14, 0.09, 0.22);
    vec3 mid = vec3(0.80, 0.43, 0.31);
    vec3 light = vec3(1.0, 0.88, 0.64);
    float stepped = floor(luma * 13.0 + 0.5) / 13.0;
    vec3 grade = mix(shadows, mid, smoothstep(0.0, 0.55, stepped));
    grade = mix(grade, light, smoothstep(0.46, 1.0, stepped));
    float vignette = 1.0 - 0.18 * dot(UV - 0.5, UV - 0.5);
    COLOR = vec4(mix(grade, source, 0.12) * vignette, 1.0);
}
