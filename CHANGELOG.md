# Changelog

## [0.3.0](https://github.com/mcthesw/Glome/compare/v0.2.0...v0.3.0) (2026-11-28)


### Features

* **audio:** report spherical arrivals and cancel pending playback ([835c4cc](https://github.com/mcthesw/Glome/commit/835c4cc0776c3d898fbcdee4775c26aa0d81b80b))
* **core:** add oriented capsule sweeps and walkable step queries ([cdc5248](https://github.com/mcthesw/Glome/commit/cdc5248728f5a81efa8b1d09332f82e8be4c3cbd))
* **editor:** bake live CSG and edit geodesic spans ([99dc5f4](https://github.com/mcthesw/Glome/commit/99dc5f4525f87ebf90b340a24172f778557438a3))
* **editor:** place linked scenes and transform chart nodes ([9fb24df](https://github.com/mcthesw/Glome/commit/9fb24dfdcf6a79229fdac3de666ebf6ad9f0a7d2))
* **editor:** update native lighting without rebaking geometry ([b480113](https://github.com/mcthesw/Glome/commit/b4801137207de7c4cd117e0aae5aec946dc870ab))
* **editor:** upgrade S3 authoring controls, async preview bakes, and clean .NET unload ([642f2ec](https://github.com/mcthesw/Glome/commit/642f2ecaf0c2ee51c964a5082153eae7184a1285))
* **editor:** use native Godot tools and menus in the viewport ([13fe392](https://github.com/mcthesw/Glome/commit/13fe392ed89bdaa90b4d2d7bb2cd901218351f2c))


### Bug Fixes

* **core:** exact S3 geodesic collision radii, vertical gaze, full-turn apertures, and BVH traversal ([2cb83a5](https://github.com/mcthesw/Glome/commit/2cb83a52e29562468fb722ba81d6391d82fd6d23))


### Performance Improvements

* **render:** eliminate render-thread draw closures, parallelize bake projection, and reuse texture banks ([b331e00](https://github.com/mcthesw/Glome/commit/b331e000df95993229a93caf431be3d6fb089b8d))
* **rendering:** parallelize cap BVH builds and batch geometry packing ([e0d8c53](https://github.com/mcthesw/Glome/commit/e0d8c539f0932674c2f5b584e3fbb0aefb684340))

## [0.2.0](https://github.com/mcthesw/Glome/compare/v0.1.0...v0.2.0) (2026-11-26)


### Features

* add geodesic ground grid, live shader reload, and passive editor bridge skill ([d768010](https://github.com/mcthesw/Glome/commit/d768010cc67d1fd740e30a196169ac729a4f469a))
* **baker:** split input extraction and worker-safe bake core ([b671e1b](https://github.com/mcthesw/Glome/commit/b671e1b1d8e18ede1de79a74fc85fa92aac1dde9))
* **collision:** expose SweepSphere helper on S3CollisionWorld ([cb03620](https://github.com/mcthesw/Glome/commit/cb036200af78fdf6ab0dfd24e634ba3a5169228f))
* **glome:** expose cancellable public BakeAsync and fix standalone addon hygiene ([fc923f0](https://github.com/mcthesw/Glome/commit/fc923f0426ceeede6e84ba312ef48dc945f00b67))
* **glome:** support walkable chart elevation decks and optional ground aperture hooks ([1bd0b6f](https://github.com/mcthesw/Glome/commit/1bd0b6fbed28bb7830000f18ef2553bc4fbcb972))
